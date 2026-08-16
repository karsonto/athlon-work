using System.Windows;
using System.Windows.Controls;

namespace Athlon.Agent.App.Controls;

public partial class ComposerContextMeterControl : UserControl
{
    public static readonly DependencyProperty IsFlyoutOpenProperty =
        DependencyProperty.Register(
            nameof(IsFlyoutOpen),
            typeof(bool),
            typeof(ComposerContextMeterControl),
            new PropertyMetadata(false));

    public ComposerContextMeterControl()
    {
        InitializeComponent();
    }

    public bool IsFlyoutOpen
    {
        get => (bool)GetValue(IsFlyoutOpenProperty);
        set => SetValue(IsFlyoutOpenProperty, value);
    }

    private void MeterButton_OnClick(object sender, RoutedEventArgs e) =>
        IsFlyoutOpen = !IsFlyoutOpen;
}
