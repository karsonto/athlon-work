using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Athlon.Agent.App.Controls;

public partial class RightSidebarToggleIcon : UserControl
{
    private const double IconTransitionDurationMs = 200;

    public static readonly DependencyProperty IsPanelOpenProperty =
        DependencyProperty.Register(
            nameof(IsPanelOpen),
            typeof(bool),
            typeof(RightSidebarToggleIcon),
            new PropertyMetadata(true, OnIsPanelOpenChanged));

    public RightSidebarToggleIcon()
    {
        InitializeComponent();
        UpdatePanelFillOpacity(IsPanelOpen, animate: false);
    }

    public bool IsPanelOpen
    {
        get => (bool)GetValue(IsPanelOpenProperty);
        set => SetValue(IsPanelOpenProperty, value);
    }

    private static void OnIsPanelOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RightSidebarToggleIcon icon && e.NewValue is bool isOpen)
        {
            icon.UpdatePanelFillOpacity(isOpen, animate: true);
        }
    }

    private void UpdatePanelFillOpacity(bool isOpen, bool animate)
    {
        var target = isOpen ? 1.0 : 0.35;
        if (!animate)
        {
            PanelFill.BeginAnimation(UIElement.OpacityProperty, null);
            PanelFill.Opacity = target;
            return;
        }

        PanelFill.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(IconTransitionDurationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
    }
}
