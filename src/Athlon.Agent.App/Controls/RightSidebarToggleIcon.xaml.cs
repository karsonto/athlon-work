using System.Windows;
using System.Windows.Controls;

namespace Athlon.Agent.App.Controls;

public partial class RightSidebarToggleIcon : UserControl
{
    public static readonly DependencyProperty IsPanelOpenProperty =
        DependencyProperty.Register(
            nameof(IsPanelOpen),
            typeof(bool),
            typeof(RightSidebarToggleIcon),
            new PropertyMetadata(true, OnIsPanelOpenChanged));

    public RightSidebarToggleIcon()
    {
        InitializeComponent();
        UpdatePanelFillOpacity(IsPanelOpen);
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
            icon.UpdatePanelFillOpacity(isOpen);
        }
    }

    private void UpdatePanelFillOpacity(bool isOpen) => PanelFill.Opacity = isOpen ? 1.0 : 0.35;
}
