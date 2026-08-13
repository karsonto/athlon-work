using System.Windows;
using System.Windows.Controls;

namespace Athlon.Agent.App.Controls;

public partial class ComposerContextMeterControl : UserControl
{
    public ComposerContextMeterControl()
    {
        InitializeComponent();
    }

    private void MeterButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.ContextOccupancyViewModel occupancy)
        {
            occupancy.IsFlyoutOpen = !occupancy.IsFlyoutOpen;
        }
    }
}
