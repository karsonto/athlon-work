using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services.Room;

namespace Athlon.Agent.App.Windows;

public partial class RoomInviteWindow : Window
{
    private readonly AthlonRoomServer _roomServer;
    private readonly string _inviteUrl;

    public RoomInviteWindow(AthlonRoomServer roomServer, string inviteUrl)
    {
        InitializeComponent();
        _roomServer = roomServer;
        _inviteUrl = inviteUrl;
        InviteUrlBox.Text = inviteUrl;
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_inviteUrl);
        }
        catch
        {
            // clipboard can fail if locked
        }
    }

    private void OpenBrowserButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _inviteUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    private async void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _roomServer.StopAsync().ConfigureAwait(true);
        }
        catch
        {
            // best effort
        }

        MessageBox.Show(
            this,
            Strings.Get("Shell_ChatRoomStopped"),
            Strings.Get("Shell_ChatRoomTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        Close();
    }
}
