using System.Windows;
using System.Windows.Controls;
using Microsoft.Xaml.Behaviors;

namespace Athlon.Agent.App.Behaviors;

public enum WindowCaptionAction
{
    Minimize,
    MaximizeRestore,
    Close
}

public sealed class WindowCaptionButtonBehavior : Behavior<Button>
{
    public static readonly DependencyProperty ActionProperty =
        DependencyProperty.Register(
            nameof(Action),
            typeof(WindowCaptionAction),
            typeof(WindowCaptionButtonBehavior),
            new PropertyMetadata(WindowCaptionAction.Close));

    public WindowCaptionAction Action
    {
        get => (WindowCaptionAction)GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.Click += OnClick;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.Click -= OnClick;
        base.OnDetaching();
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(AssociatedObject);
        if (window is null)
        {
            return;
        }

        switch (Action)
        {
            case WindowCaptionAction.Minimize:
                window.WindowState = WindowState.Minimized;
                break;
            case WindowCaptionAction.MaximizeRestore:
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                break;
            case WindowCaptionAction.Close:
                window.Close();
                break;
        }
    }
}
