using System.Windows;
using System.Windows.Input;
using Athlon.Agent.App.Services;

namespace Athlon.Agent.App.Windows;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        ProductNameText.Text = AppVersionInfo.ProductName;
        VersionText.Text = $"Version {AppVersionInfo.VersionDisplay}";
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
