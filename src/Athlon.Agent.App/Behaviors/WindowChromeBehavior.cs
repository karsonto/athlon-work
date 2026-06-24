using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    public Button? MaximizeRestoreButton
    {
        get => (Button?)GetValue(MaximizeRestoreButtonProperty);
        set => SetValue(MaximizeRestoreButtonProperty, value);
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

    private void OnWindowStateChanged(object? sender, EventArgs e) =>
        UpdateMaximizeRestoreButton();

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

        MaximizeRestoreButton.Content = _window.WindowState == WindowState.Maximized ? "❐" : "□";
        MaximizeRestoreButton.ToolTip = _window.WindowState == WindowState.Maximized ? "还原" : "最大化";
    }
}
