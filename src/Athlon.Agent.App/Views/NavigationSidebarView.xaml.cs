using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Views;

public partial class NavigationSidebarView : UserControl
{
    private MainShellViewModel? _shell;
    private bool _toolsNavReady;

    public NavigationSidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_shell is null)
        {
            return;
        }

        // Apply persisted expand state without animation on first paint.
        ApplyToolsNavExpanded(_shell.IsToolsNavExpanded, animate: false);
        _toolsNavReady = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
        }

        _shell = e.NewValue as MainShellViewModel;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
            if (IsLoaded)
            {
                ApplyToolsNavExpanded(_shell.IsToolsNavExpanded, animate: false);
                _toolsNavReady = true;
            }
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainShellViewModel.IsToolsNavExpanded) || _shell is null)
        {
            return;
        }

        ApplyToolsNavExpanded(_shell.IsToolsNavExpanded, animate: _toolsNavReady);
    }

    private void ApplyToolsNavExpanded(bool expanded, bool animate)
    {
        ToolsNavPanel.BeginAnimation(UIElement.OpacityProperty, null);
        ToolsNavTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ToolsNavChevronRotate.BeginAnimation(RotateTransform.AngleProperty, null);

        if (!animate)
        {
            ToolsNavPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            ToolsNavPanel.Opacity = expanded ? 1 : 0;
            ToolsNavTranslate.Y = expanded ? 0 : 18;
            ToolsNavChevronRotate.Angle = expanded ? 90 : 0;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(expanded ? 240 : 160);
        var ease = new CubicEase
        {
            EasingMode = expanded ? EasingMode.EaseOut : EasingMode.EaseIn
        };

        if (expanded)
        {
            ToolsNavPanel.Visibility = Visibility.Visible;
            ToolsNavPanel.Opacity = 0;
            ToolsNavTranslate.Y = 22;
        }

        var opacity = new DoubleAnimation
        {
            To = expanded ? 1 : 0,
            Duration = duration,
            EasingFunction = ease
        };
        var slide = new DoubleAnimation
        {
            To = expanded ? 0 : 14,
            Duration = duration,
            EasingFunction = ease
        };
        var chevron = new DoubleAnimation
        {
            To = expanded ? 90 : 0,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        if (!expanded)
        {
            opacity.Completed += (_, _) =>
            {
                if (_shell is { IsToolsNavExpanded: false })
                {
                    ToolsNavPanel.Visibility = Visibility.Collapsed;
                }
            };
        }

        ToolsNavPanel.BeginAnimation(UIElement.OpacityProperty, opacity);
        ToolsNavTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
        ToolsNavChevronRotate.BeginAnimation(RotateTransform.AngleProperty, chevron);
    }
}
