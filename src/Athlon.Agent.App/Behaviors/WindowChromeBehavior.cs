using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Athlon.Agent.App.Resources;
using Microsoft.Xaml.Behaviors;

namespace Athlon.Agent.App.Behaviors;

public sealed class WindowChromeBehavior : Behavior<FrameworkElement>
{
    public static readonly DependencyProperty MaximizeRestoreButtonProperty =
        DependencyProperty.Register(
            nameof(MaximizeRestoreButton),
            typeof(Button),
            typeof(WindowChromeBehavior),
            new PropertyMetadata(null));

    public static readonly DependencyProperty FrameBorderProperty =
        DependencyProperty.Register(
            nameof(FrameBorder),
            typeof(Border),
            typeof(WindowChromeBehavior),
            new PropertyMetadata(null));

    public Button? MaximizeRestoreButton
    {
        get => (Button?)GetValue(MaximizeRestoreButtonProperty);
        set => SetValue(MaximizeRestoreButtonProperty, value);
    }

    /// <summary>
    /// Optional outer frame border. Corner radius / border thickness track window state
    /// (rounded when restored, square when maximized).
    /// </summary>
    public Border? FrameBorder
    {
        get => (Border?)GetValue(FrameBorderProperty);
        set => SetValue(FrameBorderProperty, value);
    }

    private Window? _window;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.Loaded += OnAssociatedLoaded;
        AssociatedObject.MouseLeftButtonDown += OnTitleBarMouseLeftButtonDown;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.Loaded -= OnAssociatedLoaded;
        AssociatedObject.MouseLeftButtonDown -= OnTitleBarMouseLeftButtonDown;
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
        }

        base.OnDetaching();
    }

    private void OnAssociatedLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(AssociatedObject);
        if (_window is null)
        {
            return;
        }

        _window.StateChanged += OnWindowStateChanged;
        UpdateMaximizeRestoreButton();
        SyncFrameForWindowState();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_window is null || IsWithinTitleBarMenu(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            _window.DragMove();
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeRestoreButton();
        SyncFrameForWindowState();
    }

    private void SyncFrameForWindowState()
    {
        if (_window is null)
        {
            return;
        }

        var maximized = _window.WindowState == WindowState.Maximized;
        var radius = maximized ? default : AppLayoutMetrics.MainWindowCornerRadius;

        var chrome = WindowChrome.GetWindowChrome(_window);
        if (chrome is not null)
        {
            chrome.CornerRadius = radius;
        }

        if (FrameBorder is null)
        {
            return;
        }

        FrameBorder.CornerRadius = radius;
        FrameBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        foreach (var behavior in Interaction.GetBehaviors(FrameBorder))
        {
            if (behavior is RoundedClipBehavior clip)
            {
                clip.CornerRadius = radius;
                break;
            }
        }
    }

    private static bool IsWithinTitleBarMenu(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Menu or MenuItem)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void ToggleWindowState()
    {
        if (_window is null)
        {
            return;
        }

        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeRestoreButton()
    {
        if (MaximizeRestoreButton is null || _window is null)
        {
            return;
        }

        var isMaximized = _window.WindowState == WindowState.Maximized;
        MaximizeRestoreButton.Content = null;
        MaximizeRestoreButton.ContentTemplate = (DataTemplate)MaximizeRestoreButton.FindResource(
            isMaximized ? "WindowCaptionRestoreIconTemplate" : "WindowCaptionMaximizeIconTemplate");
        MaximizeRestoreButton.ToolTip = isMaximized ? Strings.Get("Common_Restore") : Strings.Get("Common_Maximize");
    }
}
