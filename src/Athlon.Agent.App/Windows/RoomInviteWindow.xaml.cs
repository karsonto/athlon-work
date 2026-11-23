using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services.Room;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Skills;

namespace Athlon.Agent.App.Windows;

public partial class RoomInviteWindow : Window
{
    private readonly AthlonRoomServer _roomServer;
    private readonly RoomAgentBridge _agentBridge;
    private readonly RoomAgentProfileStore _profileStore;
    private readonly string _inviteUrl;
    private readonly ObservableCollection<CapOption> _tools = new();
    private readonly ObservableCollection<CapOption> _skills = new();
    private readonly ObservableCollection<CapOption> _mcp = new();

    public RoomInviteWindow(
        AthlonRoomServer roomServer,
        RoomAgentBridge agentBridge,
        RoomAgentProfileStore profileStore,
        IAgentSkillCatalog skillCatalog,
        AppSettings settings,
        string inviteUrl)
    {
        InitializeComponent();
        _roomServer = roomServer;
        _agentBridge = agentBridge;
        _profileStore = profileStore;
        _inviteUrl = inviteUrl;
        InviteUrlBox.Text = inviteUrl;

        var profile = _profileStore.GetOrCreate();
        foreach (var tool in RoomAgentProfileStore.CatalogTools)
        {
            _tools.Add(new CapOption(
                tool,
                tool,
                profile.ToolAllowList.Any(t => string.Equals(t, tool, StringComparison.OrdinalIgnoreCase))));
        }

        foreach (var skill in skillCatalog.Skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = skill.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            _skills.Add(new CapOption(
                name,
                name,
                profile.SkillNames.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))));
        }

        foreach (var server in settings.McpServers.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Name)))
        {
            _mcp.Add(new CapOption(
                server.Name,
                server.Name,
                profile.McpServerNames.Any(m => string.Equals(m, server.Name, StringComparison.OrdinalIgnoreCase))));
        }

        ToolList.ItemsSource = _tools;
        SkillList.ItemsSource = _skills;
        McpList.ItemsSource = _mcp;
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        PersistCapabilities();
        Close();
    }

    private void SaveCapsButton_OnClick(object sender, RoutedEventArgs e)
    {
        PersistCapabilities();
        MessageBox.Show(
            this,
            Strings.Get("Shell_ChatRoomCapsSaved"),
            Strings.Get("Shell_ChatRoomTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void PersistCapabilities()
    {
        var profile = _profileStore.GetOrCreate();
        profile.ToolAllowList = _tools.Where(t => t.IsChecked).Select(t => t.Id).ToList();
        profile.SkillNames = _skills.Where(t => t.IsChecked).Select(t => t.Id).ToList();
        profile.McpServerNames = _mcp.Where(t => t.IsChecked).Select(t => t.Id).ToList();
        if (!profile.ToolAllowList.Any(t => string.Equals(t, "room_share_file", StringComparison.OrdinalIgnoreCase)))
        {
            profile.ToolAllowList.Add("room_share_file");
        }

        _profileStore.Save(profile);
    }

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
            _agentBridge.DetachAll();
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

    public sealed class CapOption : INotifyPropertyChanged
    {
        private bool _isChecked;

        public CapOption(string id, string label, bool isChecked)
        {
            Id = id;
            Label = label;
            _isChecked = isChecked;
        }

        public string Id { get; }

        public string Label { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                {
                    return;
                }

                _isChecked = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
