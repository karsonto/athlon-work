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
    private readonly RoomStore _roomStore;
    private readonly ObservableCollection<CapOption> _tools = new();
    private readonly ObservableCollection<CapOption> _skills = new();
    private readonly ObservableCollection<CapOption> _mcp = new();
    private readonly ObservableCollection<RoomListItem> _rooms = new();
    private readonly ObservableCollection<WorkspaceOption> _workspaces = new();
    private readonly Dictionary<string, IDisposable> _unreadSubs = new(StringComparer.OrdinalIgnoreCase);
    private RoomListItem? _selected;
    private bool _suppressWorkspaceChange;

    public RoomInviteWindow(
        AthlonRoomServer roomServer,
        RoomAgentBridge agentBridge,
        RoomAgentProfileStore profileStore,
        RoomStore roomStore,
        IAgentSkillCatalog skillCatalog,
        AppSettings settings)
    {
        InitializeComponent();
        _roomServer = roomServer;
        _agentBridge = agentBridge;
        _profileStore = profileStore;
        _roomStore = roomStore;

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

        foreach (var workspace in settings.Workspaces.Where(w => !string.IsNullOrWhiteSpace(w.RootPath)))
        {
            var label = string.IsNullOrWhiteSpace(workspace.Name)
                ? workspace.RootPath
                : $"{workspace.Name} ({workspace.RootPath})";
            _workspaces.Add(new WorkspaceOption(workspace.Id, workspace.RootPath, label));
        }

        ToolList.ItemsSource = _tools;
        SkillList.ItemsSource = _skills;
        McpList.ItemsSource = _mcp;
        RoomList.ItemsSource = _rooms;
        WorkspaceCombo.ItemsSource = _workspaces;

        Closed += (_, _) => DisposeUnreadSubs();
        RefreshBindWarning();
        RefreshRoomList(selectFirst: true);
    }

    private void RefreshBindWarning()
    {
        if (_roomServer.IsLanReachable)
        {
            BindWarningBanner.Visibility = Visibility.Collapsed;
            BindWarningText.Text = string.Empty;
            CopyLinkButton.IsEnabled = true;
            return;
        }

        BindWarningBanner.Visibility = Visibility.Visible;
        BindWarningText.Text = string.Format(
            Strings.Get("Shell_ChatRoomBindLoopbackOnly"),
            _roomServer.Port,
            _roomServer.BindHint);
        CopyLinkButton.IsEnabled = false;
    }

    private void RefreshRoomList(bool selectFirst, string? preferId = null)
    {
        DisposeUnreadSubs();
        _rooms.Clear();
        foreach (var summary in _roomServer.ListActiveRooms())
        {
            var item = new RoomListItem(summary);
            _rooms.Add(item);
            AttachUnread(item);
        }

        if (_rooms.Count == 0)
        {
            _selected = null;
            InviteUrlBox.Text = string.Empty;
            SyncWorkspaceCombo(null);
            return;
        }

        RoomListItem? pick = null;
        if (!string.IsNullOrWhiteSpace(preferId))
        {
            pick = _rooms.FirstOrDefault(r => string.Equals(r.Id, preferId, StringComparison.OrdinalIgnoreCase));
        }

        pick ??= selectFirst ? _rooms[0] : _rooms.FirstOrDefault(r => r.Id == _selected?.Id) ?? _rooms[0];
        RoomList.SelectedItem = pick;
        ApplySelection(pick);
    }

    private void AttachUnread(RoomListItem item)
    {
        var sub = _roomStore.SubscribeEvents(item.Id, ev =>
        {
            if (ev.Kind != RoomStoreEventKind.Message)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (_selected is not null
                    && string.Equals(_selected.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (string.Equals(ev.Message.SenderId, RoomAgentIds.SenderId, StringComparison.OrdinalIgnoreCase))
                {
                    // still count as unread for host monitoring
                }

                item.Unread++;
            });
        });
        _unreadSubs[item.Id] = sub;
    }

    private void DisposeUnreadSubs()
    {
        foreach (var sub in _unreadSubs.Values)
        {
            sub.Dispose();
        }

        _unreadSubs.Clear();
    }

    private void ApplySelection(RoomListItem? item)
    {
        _selected = item;
        RefreshBindWarning();
        if (item is null)
        {
            InviteUrlBox.Text = string.Empty;
            SyncWorkspaceCombo(null);
            return;
        }

        item.Unread = 0;
        // Share URL only when LAN-reachable; otherwise show local URL for host preview only.
        var share = _roomServer.GetInviteUrl(item.Id);
        var local = _roomServer.GetLocalInviteUrl(item.Id);
        if (!string.IsNullOrWhiteSpace(share))
        {
            InviteUrlBox.Text = share;
        }
        else if (!string.IsNullOrWhiteSpace(local))
        {
            InviteUrlBox.Text = string.Format(
                Strings.Get("Shell_ChatRoomLocalOnlyInvite"),
                local);
        }
        else
        {
            InviteUrlBox.Text = string.Empty;
        }

        SyncWorkspaceCombo(item);
    }

    private void SyncWorkspaceCombo(RoomListItem? item)
    {
        _suppressWorkspaceChange = true;
        try
        {
            if (item is null || _workspaces.Count == 0)
            {
                WorkspaceCombo.SelectedItem = null;
                return;
            }

            var match = _workspaces.FirstOrDefault(w =>
                (!string.IsNullOrWhiteSpace(item.WorkspaceId)
                 && string.Equals(w.Id, item.WorkspaceId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(item.WorkspaceRoot)
                    && string.Equals(w.RootPath, item.WorkspaceRoot, StringComparison.OrdinalIgnoreCase)));
            WorkspaceCombo.SelectedItem = match ?? _workspaces.FirstOrDefault();
        }
        finally
        {
            _suppressWorkspaceChange = false;
        }
    }

    private void WorkspaceCombo_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressWorkspaceChange || _selected is null)
        {
            return;
        }

        if (WorkspaceCombo.SelectedItem is not WorkspaceOption option)
        {
            return;
        }

        var updated = _roomServer.SetRoomWorkspace(_selected.Id, option.RootPath, option.Id);
        if (updated is null)
        {
            return;
        }

        _selected.WorkspaceId = updated.WorkspaceId;
        _selected.WorkspaceRoot = updated.WorkspaceRoot;
        _selected.WorkspaceLabel = string.IsNullOrWhiteSpace(updated.WorkspaceRoot)
            ? Strings.Get("Shell_ChatRoomNoWorkspace")
            : option.Label;
    }

    private void RoomList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplySelection(RoomList.SelectedItem as RoomListItem);
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
        if (_selected is null || !_roomServer.IsLanReachable)
        {
            return;
        }

        var url = _roomServer.GetInviteUrl(_selected.Id);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Clipboard.SetText(url);
        }
        catch
        {
            // clipboard can fail if locked
        }
    }

    private void OpenBrowserButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var url = _roomServer.GetLocalInviteUrl(_selected.Id)
            ?? _roomServer.GetInviteUrl(_selected.Id);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    private async void NewRoomButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var option = WorkspaceCombo.SelectedItem as WorkspaceOption ?? _workspaces.FirstOrDefault();
            var result = await _roomServer
                .StartOrCreateRoomAsync(
                    workspaceRoot: option?.RootPath,
                    workspaceId: option?.Id)
                .ConfigureAwait(true);
            _agentBridge.AttachRoom(result.Room.Id);
            RefreshBindWarning();
            RefreshRoomList(selectFirst: false, preferId: result.Room.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                string.Format(Strings.Get("Shell_ChatRoomStartFailed"), ex.Message),
                Strings.Get("Shell_ChatRoomTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void CloseRoomButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var id = _selected.Id;
        try
        {
            await _roomServer.CloseRoomAsync(id).ConfigureAwait(true);
            // Bridge detach via lifecycle hook
            RefreshRoomList(selectFirst: true);
        }
        catch
        {
            // best effort
        }
    }

    private async void StopAllButton_OnClick(object sender, RoutedEventArgs e)
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

    public sealed class RoomListItem : INotifyPropertyChanged
    {
        private int _unread;
        private string _workspaceLabel;

        public RoomListItem(RoomSummary summary)
        {
            Id = summary.Id;
            Title = summary.Title;
            InviteUrl = summary.InviteUrl;
            WorkspaceRoot = summary.WorkspaceRoot;
            WorkspaceId = summary.WorkspaceId;
            CreatedAtLabel = summary.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
            _workspaceLabel = string.IsNullOrWhiteSpace(summary.WorkspaceName)
                ? (summary.WorkspaceRoot ?? Strings.Get("Shell_ChatRoomNoWorkspace"))
                : summary.WorkspaceName!;
        }

        public string Id { get; }

        public string Title { get; }

        public string InviteUrl { get; }

        public string CreatedAtLabel { get; }

        public string? WorkspaceRoot { get; set; }

        public string? WorkspaceId { get; set; }

        public string WorkspaceLabel
        {
            get => _workspaceLabel;
            set
            {
                if (_workspaceLabel == value)
                {
                    return;
                }

                _workspaceLabel = value;
                OnPropertyChanged();
            }
        }

        public int Unread
        {
            get => _unread;
            set
            {
                if (_unread == value)
                {
                    return;
                }

                _unread = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UnreadLabel));
                OnPropertyChanged(nameof(UnreadVisibility));
            }
        }

        public string UnreadLabel => _unread > 99 ? "99+" : _unread.ToString();

        public Visibility UnreadVisibility => _unread > 0 ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class WorkspaceOption(string id, string rootPath, string label)
    {
        public string Id { get; } = id;

        public string RootPath { get; } = rootPath;

        public string Label { get; } = label;
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
