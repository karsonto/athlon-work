using System.Windows;
using System.Windows.Input;
using Athlon.Agent.App.Services;

namespace Athlon.Agent.App.Windows;

public partial class AboutWindow : Window
{
    private readonly AppUpdateService _updateService;

    public AboutWindow(AppUpdateService updateService)
    {
        _updateService = updateService;
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

    private async void CheckUpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "正在检查更新…";

        var result = await _updateService.CheckAndPromptAsync();
        UpdateStatusText.Text = result.Message;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
