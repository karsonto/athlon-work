using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(ChatMessage message, bool expandTool = false)
    {
        Role = message.Role.ToString();
        Content = message.Content;
        CreatedAt = message.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        IsUser = message.Role == MessageRole.User;
        IsTool = message.Role == MessageRole.Tool;
        DisplayRole = IsUser ? "您" : IsTool ? "工具" : "Athlon 助手";

        if (IsTool)
        {
            ParseToolContent(message.Content, out var toolCallId, out var header, out var summary, out var detail);
            ToolCallId = toolCallId;
            ToolHeader = header;
            ToolSummary = summary;
            ToolDetail = detail;
            IsToolRunning = false;
            IsExpanded = expandTool;
        }
        else
        {
            ToolCallId = null;
            ToolHeader = string.Empty;
            ToolSummary = string.Empty;
            ToolDetail = string.Empty;
            IsToolRunning = false;
        }
    }

    private ChatMessageViewModel(AgentToolCall toolCall)
    {
        Role = MessageRole.Tool.ToString();
        ToolCallId = toolCall.Id;
        IsUser = false;
        IsTool = true;
        DisplayRole = "工具";
        IsToolRunning = true;
        CreatedAt = DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss");
        ToolHeader = $"Tool `{toolCall.Name}` running...";
        ToolSummary = FormatArgumentsPreview(toolCall.Arguments);
        ToolDetail = string.Empty;
        Content = string.Empty;
        IsExpanded = false;
    }

    public string Role { get; private set; }
    public string Content { get; private set; }
    public string CreatedAt { get; private set; }
    public bool IsUser { get; }
    public bool IsTool { get; }
    public bool AssistantTone => !IsUser;
    public string DisplayRole { get; }

    public string? ToolCallId { get; private set; }

    [ObservableProperty]
    private bool _isToolRunning;

    [ObservableProperty]
    private string _toolHeader = string.Empty;

    [ObservableProperty]
    private string _toolSummary = string.Empty;

    [ObservableProperty]
    private string _toolDetail = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    public string ChevronGlyph => IsExpanded ? "▼" : "▶";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ChevronGlyph));

    [RelayCommand]
    private void ToggleToolExpand() => IsExpanded = !IsExpanded;

    public static ChatMessageViewModel CreatePendingTool(AgentToolCall toolCall) => new(toolCall);

    public void ApplyCompletedTool(ChatMessage message)
    {
        if (!IsTool)
        {
            return;
        }

        Content = message.Content;
        CreatedAt = message.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        ParseToolContent(message.Content, out var toolCallId, out var header, out var summary, out var detail);
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            ToolCallId = toolCallId;
        }

        ToolHeader = header;
        ToolSummary = summary;
        ToolDetail = detail;
        IsToolRunning = false;
    }

    public void MarkToolCancelled()
    {
        if (!IsTool || !IsToolRunning)
        {
            return;
        }

        IsToolRunning = false;
        ToolSummary = "已停止";
    }

    private static void ParseToolContent(string content, out string? toolCallId, out string header, out string summary, out string detail)
    {
        toolCallId = null;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        header = "工具调用";
        summary = string.Empty;

        foreach (var line in lines)
        {
            if (line.StartsWith("ToolCallId:", StringComparison.OrdinalIgnoreCase))
            {
                toolCallId = line["ToolCallId:".Length..].Trim();
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line) && header == "工具调用")
            {
                header = line.Trim();
            }

            if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                summary = line["Summary:".Length..].Trim();
            }
        }

        detail = content.Trim();
    }

    private static string FormatArgumentsPreview(IReadOnlyDictionary<string, string> arguments) =>
        arguments.Count == 0
            ? string.Empty
            : string.Join("; ", arguments.Select(argument => $"{argument.Key}={argument.Value}"));
}

public sealed class SessionHistoryItemViewModel
{
    public SessionHistoryItemViewModel(SessionIndexEntry entry, bool isActive)
    {
        Id = entry.Id;
        Title = string.IsNullOrWhiteSpace(entry.Title) ? "未命名对话" : entry.Title;
        UpdatedAtText = entry.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");
        IsActive = isActive;
    }

    public string Id { get; }
    public string Title { get; }
    public string UpdatedAtText { get; }
    public bool IsActive { get; }
}

public sealed partial class ContextSidebarViewModel : ObservableObject
{
    private readonly IAppPathProvider _paths;
    private readonly IAgentSkillCatalog _skillCatalog;

    public ContextSidebarViewModel(IAppPathProvider paths, IAgentSkillCatalog skillCatalog, AppSettings settings)
    {
        _paths = paths;
        _skillCatalog = skillCatalog;
        Refresh(settings);
    }

    public ObservableCollection<string> Skills { get; } = new();
    public ObservableCollection<string> McpServers { get; } = new();
    public ObservableCollection<WorkspaceTreeNodeViewModel> WorkspaceTree { get; } = new();
    public string LocalModelStatus { get; set; } = "Local Model Active";

    public void Refresh(AppSettings settings)
    {
        _skillCatalog.Reload();
        Skills.Clear();

        var disabled = new HashSet<string>(
            settings.Skills.Where(skill => !skill.Enabled).Select(skill => skill.Name),
            StringComparer.OrdinalIgnoreCase);

        if (_skillCatalog.Skills.Count == 0)
        {
            Skills.Add($"未安装技能 ({_paths.SkillsPath})");
        }
        else
        {
            foreach (var skill in _skillCatalog.Skills.OrderBy(skill => skill.Name, StringComparer.Ordinal))
            {
                var status = disabled.Contains(skill.Name) ? "○" : "●";
                Skills.Add($"{status} {skill.Name}");
            }
        }

        McpServers.Clear();
        if (settings.McpServers.Count == 0)
        {
            McpServers.Add("未配置 MCP 服务器");
        }
        else
        {
            foreach (var server in settings.McpServers)
            {
                var status = server.Enabled ? "●" : "○";
                var command = string.IsNullOrWhiteSpace(server.Command) ? string.Empty : $"  {server.Command}";
                McpServers.Add($"{status} {server.Name}{command}");
            }
        }
    }

    public void RefreshWorkspaceTree(string? workspaceRootPath, IReadOnlyList<string> ignorePatterns)
    {
        WorkspaceTree.Clear();
        foreach (var node in WorkspaceTreeNodeViewModel.BuildTree(workspaceRootPath, ignorePatterns))
        {
            WorkspaceTree.Add(node);
        }
    }
}

public sealed partial class McpServerItemViewModel : ObservableObject
{
    public McpServerItemViewModel(McpServerSettings settings)
    {
        Settings = settings;
    }

    public McpServerSettings Settings { get; }

    public string DisplayInitial => string.IsNullOrWhiteSpace(Name) ? "M" : Name.Trim()[0].ToString().ToUpperInvariant();

    public string Name
    {
        get => Settings.Name;
        set
        {
            if (Settings.Name == value)
            {
                return;
            }

            Settings.Name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayInitial));
        }
    }

    public bool Enabled
    {
        get => Settings.Enabled;
        set
        {
            if (Settings.Enabled == value)
            {
                return;
            }

            Settings.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ToolSummary));
        }
    }

    public string Command
    {
        get => Settings.Command;
        set
        {
            if (Settings.Command == value)
            {
                return;
            }

            Settings.Command = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandSummary));
        }
    }

    public string ArgsText
    {
        get => string.Join(" ", Settings.Args);
        set
        {
            Settings.Args.Clear();
            if (!string.IsNullOrWhiteSpace(value))
            {
                Settings.Args.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandSummary));
        }
    }

    public string StatusText => Enabled ? "Configured and enabled" : "Configured but disabled";

    public string ToolSummary => Enabled ? "0 tools configured" : "Disabled";

    public string CommandSummary
    {
        get
        {
            var args = ArgsText;
            return string.IsNullOrWhiteSpace(args) ? $"command: {Command}" : $"command: {Command} {args}";
        }
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(AppSettings settings)
    {
        Settings = settings;
        foreach (var server in Settings.McpServers)
        {
            McpServers.Add(new McpServerItemViewModel(server));
        }

        SelectedMcpServer = McpServers.FirstOrDefault();
    }

    public AppSettings Settings { get; }
    public string[] Sections { get; } = { "Models", "Logging", "MCP", "Skills", "Workspace", "Tool Permissions", "Appearance" };
    public ObservableCollection<McpServerItemViewModel> McpServers { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMcpServer))]
    [NotifyPropertyChangedFor(nameof(EditableMcpArgs))]
    private McpServerItemViewModel? selectedMcpServer;

    public bool HasSelectedMcpServer => SelectedMcpServer is not null;

    public SkillSettings EditableSkill
    {
        get
        {
            if (Settings.Skills.Count == 0)
            {
                Settings.Skills.Add(new SkillSettings());
            }

            return Settings.Skills[0];
        }
    }

    public McpServerSettings EditableMcpServer
    {
        get
        {
            if (SelectedMcpServer is null)
            {
                AddMcpServer();
            }

            return SelectedMcpServer!.Settings;
        }
    }

    public string EditableMcpArgs
    {
        get => SelectedMcpServer?.ArgsText ?? string.Empty;
        set
        {
            if (SelectedMcpServer is not null)
            {
                SelectedMcpServer.ArgsText = value;
            }
        }
    }

    [RelayCommand]
    private void AddMcpServer()
    {
        var nextIndex = Settings.McpServers.Count + 1;
        var server = new McpServerSettings
        {
            Name = $"custom-mcp-{nextIndex}",
            Command = "npx",
            Enabled = true
        };
        server.Args.Add("-y");

        Settings.McpServers.Add(server);
        var item = new McpServerItemViewModel(server);
        McpServers.Add(item);
        SelectedMcpServer = item;
    }

    [RelayCommand]
    private void SelectMcpServer(McpServerItemViewModel server)
    {
        SelectedMcpServer = server;
    }

    public WorkspaceSettings EditableWorkspace
    {
        get
        {
            if (Settings.Workspaces.Count == 0)
            {
                Settings.Workspaces.Add(new WorkspaceSettings());
            }

            return Settings.Workspaces[0];
        }
    }
}

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IFileStorageService _storage;
    private readonly ICredentialStore _credentialStore;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly AppSettings _appSettings;
    private FileSystemWatcher? _workspaceWatcher;
    private CancellationTokenSource? _turnCancellation;
    private AgentSession _session = AgentSession.Create("New Chat");

    public MainWindowViewModel(
        IAgentOrchestrator orchestrator,
        IFileStorageService storage,
        ICredentialStore credentialStore,
        IActiveWorkspaceContext workspaceContext,
        IAppPathProvider paths,
        IAgentSkillCatalog skillCatalog,
        AppSettings settings)
    {
        _orchestrator = orchestrator;
        _storage = storage;
        _credentialStore = credentialStore;
        _workspaceContext = workspaceContext;
        _appSettings = settings;
        Settings = new SettingsViewModel(settings);
        Sidebar = new ContextSidebarViewModel(paths, skillCatalog, settings);
        HasStoredApiKey = EnsureCurrentApiKeySecret(settings);
        ApplySessionWorkspace();
        _ = InitializeAsync();
    }

    public async Task InitializeAsync()
    {
        await RefreshSessionHistoryAsync();
        var latest = SessionHistory.FirstOrDefault();
        if (latest is not null && _session.Messages.Count == 0)
        {
            await LoadSessionInternalAsync(latest.Id);
        }
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public ObservableCollection<SessionHistoryItemViewModel> SessionHistory { get; } = new();
    public ContextSidebarViewModel Sidebar { get; }
    public SettingsViewModel Settings { get; }

    private CancellationTokenSource? _copyNoticeCts;

    [ObservableProperty]
    private string copyNotice = string.Empty;

    [ObservableProperty]
    private bool isCopyNoticeVisible;

    [ObservableProperty]
    private string composerText = string.Empty;

    public bool IsComposerEmpty => string.IsNullOrWhiteSpace(ComposerText);

    [ObservableProperty]
    private string streamingText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string currentPage = "Chat";

    [ObservableProperty]
    private string currentSessionTitle = "New Chat";

    [ObservableProperty]
    private string settingsStatus = "Settings are stored as JSON files under the app data folder.";

    [ObservableProperty]
    private string apiKey = string.Empty;

    [ObservableProperty]
    private bool hasStoredApiKey;

    [ObservableProperty]
    private string activeWorkspaceName = "No workspace";

    public bool IsChatPage => CurrentPage == "Chat";
    public bool IsSettingsPage => CurrentPage == "Settings";

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = page;
    }

    [RelayCommand]
    private async Task NewSession()
    {
        _turnCancellation?.Cancel();
        await SaveCurrentSessionIfNeededAsync();
        _session = AgentSession.Create("New Chat");
        CurrentSessionTitle = _session.Title;
        ComposerText = string.Empty;
        StreamingText = string.Empty;
        IsBusy = false;
        Messages.Clear();
        CurrentPage = "Chat";
        ApplySessionWorkspace();
        await RefreshSessionHistoryAsync();
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task LoadSessionAsync(SessionHistoryItemViewModel? item)
    {
        if (item is null || item.Id == _session.Id)
        {
            return;
        }

        _turnCancellation?.Cancel();
        await SaveCurrentSessionIfNeededAsync();
        await LoadSessionInternalAsync(item.Id);
        CurrentPage = "Chat";
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionHistoryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"确定删除对话「{item.Title}」吗？此操作无法撤销。",
            "删除对话",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await _storage.DeleteSessionAsync(item.Id);

        if (string.Equals(_session.Id, item.Id, StringComparison.Ordinal))
        {
            _session = AgentSession.Create("New Chat");
            CurrentSessionTitle = _session.Title;
            ComposerText = string.Empty;
            StreamingText = string.Empty;
            Messages.Clear();
            ApplySessionWorkspace();
            CurrentPage = "Chat";
        }

        await RefreshSessionHistoryAsync();
        SettingsStatus = "对话已删除。";
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(ComposerText))
        {
            return;
        }

        var input = ComposerText.Trim();
        ComposerText = string.Empty;
        IsBusy = true;
        StreamingText = string.Empty;
        _turnCancellation = new CancellationTokenSource();
        Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, input)));
        SyncWorkspaceContext();

        try
        {
            var callbacks = new AgentTurnCallbacks
            {
                OnToolStarted = async toolCall => await RunOnUiAsync(() =>
                {
                    if (FindToolMessage(toolCall.Id) is not null)
                    {
                        return;
                    }

                    Messages.Add(ChatMessageViewModel.CreatePendingTool(toolCall));
                }),
                OnMessage = async message => await RunOnUiAsync(() =>
                {
                    if (message.Role == MessageRole.Tool)
                    {
                        var toolCallId = ExtractToolCallId(message.Content);
                        var existing = FindToolMessage(toolCallId);
                        if (existing is not null)
                        {
                            existing.ApplyCompletedTool(message);
                            return;
                        }

                        Messages.Add(new ChatMessageViewModel(message));
                        return;
                    }

                    Messages.Add(new ChatMessageViewModel(message));
                }),
                OnAssistantTextDelta = async token => await RunOnUiAsync(() =>
                {
                    StreamingText += token;
                })
            };

            _session = await _orchestrator.SendAsync(_session, input, callbacks, _turnCancellation.Token);
            CurrentSessionTitle = _session.Title;
            await SaveCurrentSessionIfNeededAsync();
        }
        catch (OperationCanceledException)
        {
            await RunOnUiAsync(() =>
            {
                foreach (var message in Messages.Where(static message => message.IsToolRunning))
                {
                    message.MarkToolCancelled();
                }

                Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.System, "生成已停止。")));
            });
            await SaveCurrentSessionIfNeededAsync();
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.System, $"模型调用失败：{ex.Message}")));
            await SaveCurrentSessionIfNeededAsync();
        }
        finally
        {
            StreamingText = string.Empty;
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private ChatMessageViewModel? FindToolMessage(string? toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return null;
        }

        return Messages.LastOrDefault(message =>
            message.IsTool && string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal));
    }

    private static string? ExtractToolCallId(string content)
    {
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("ToolCallId:", StringComparison.OrdinalIgnoreCase))
            {
                return line["ToolCallId:".Length..].Trim();
            }
        }

        return null;
    }

    private Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    [RelayCommand]
    private void Stop() => _turnCancellation?.Cancel();

    [RelayCommand]
    private async Task ConfigureWorkspaceAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select agent workspace",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_session.ActiveWorkspace) && Directory.Exists(_session.ActiveWorkspace))
        {
            dialog.InitialDirectory = _session.ActiveWorkspace;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var folderName = new DirectoryInfo(dialog.FolderName).Name;
        _session = _session.WithWorkspace(dialog.FolderName);
        ApplySessionWorkspace();
        await SaveCurrentSessionIfNeededAsync();
        SettingsStatus = $"当前对话工作区：{folderName}";
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            await _credentialStore.SaveSecretAsync(ModelSettings.ApiKeySecretName, ApiKey);
            ApiKey = string.Empty;
            HasStoredApiKey = true;
            OnPropertyChanged(nameof(ApiKey));
        }

        Settings.Settings.Model.LegacyApiKeyCredentialName = null;
        await _storage.SaveSettingsAsync(Settings.Settings);
        Sidebar.Refresh(Settings.Settings);
        ApplySessionWorkspace();
        OnPropertyChanged(nameof(Sidebar));
        SettingsStatus = $"Saved at {DateTime.Now:HH:mm:ss}";
    }

    private bool CanSend() => !IsBusy;

    private void ApplySessionWorkspace()
    {
        SyncWorkspaceContext();
        ActiveWorkspaceName = string.IsNullOrWhiteSpace(_workspaceContext.DisplayName) ? "未配置工作区" : _workspaceContext.DisplayName!;
        Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
        ConfigureWorkspaceWatcher();
        OnPropertyChanged(nameof(Sidebar));
    }

    private void SyncWorkspaceContext()
    {
        _workspaceContext.SetWorkspace(_session.ActiveWorkspace);
    }

    private async Task SaveCurrentSessionIfNeededAsync()
    {
        if (_session.Messages.Count == 0)
        {
            return;
        }

        _session = DeriveSessionTitle(_session);
        await _storage.SaveSessionAsync(_session);
        await RefreshSessionHistoryAsync();
    }

    private async Task RefreshSessionHistoryAsync()
    {
        var entries = await _storage.ListSessionsAsync();
        SessionHistory.Clear();
        foreach (var entry in entries)
        {
            SessionHistory.Add(new SessionHistoryItemViewModel(entry, entry.Id == _session.Id));
        }

        OnPropertyChanged(nameof(HasSessionHistory));
    }

    public bool HasSessionHistory => SessionHistory.Count > 0;

    private async Task LoadSessionInternalAsync(string sessionId)
    {
        var loaded = await _storage.LoadSessionAsync(sessionId);
        if (loaded is null)
        {
            SettingsStatus = "无法加载该对话。";
            return;
        }

        _session = loaded;
        CurrentSessionTitle = _session.Title;
        ComposerText = string.Empty;
        StreamingText = string.Empty;
        Messages.Clear();

        foreach (var message in _session.Messages)
        {
            Messages.Add(new ChatMessageViewModel(message));
        }

        ApplySessionWorkspace();
        await RefreshSessionHistoryAsync();
        SettingsStatus = $"已加载对话：{_session.Title}";
    }

    private static AgentSession DeriveSessionTitle(AgentSession session)
    {
        if (!string.Equals(session.Title, "New Chat", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(session.Title, "New chat", StringComparison.OrdinalIgnoreCase))
        {
            return session;
        }

        var firstUser = session.Messages.FirstOrDefault(message => message.Role == MessageRole.User);
        if (firstUser is null || string.IsNullOrWhiteSpace(firstUser.Content))
        {
            return session;
        }

        var normalized = firstUser.Content.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        var title = normalized.Length <= 30 ? normalized : $"{normalized[..30]}...";
        return session.WithTitle(title);
    }

    private void ConfigureWorkspaceWatcher()
    {
        _workspaceWatcher?.Dispose();
        _workspaceWatcher = null;

        if (string.IsNullOrWhiteSpace(_session.ActiveWorkspace) || !Directory.Exists(_session.ActiveWorkspace))
        {
            return;
        }

        _workspaceWatcher = new FileSystemWatcher(_session.ActiveWorkspace)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };
        _workspaceWatcher.Created += WorkspaceChanged;
        _workspaceWatcher.Deleted += WorkspaceChanged;
        _workspaceWatcher.Changed += WorkspaceChanged;
        _workspaceWatcher.Renamed += WorkspaceChanged;
    }

    private void WorkspaceChanged(object sender, FileSystemEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
            Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns));
    }

    public void Dispose()
    {
        _workspaceWatcher?.Dispose();
        _turnCancellation?.Dispose();
    }

    private bool EnsureCurrentApiKeySecret(AppSettings settings)
    {
        var hasCurrentSecret = _credentialStore.HasSecretAsync(ModelSettings.ApiKeySecretName).GetAwaiter().GetResult();
        if (hasCurrentSecret || string.IsNullOrWhiteSpace(settings.Model.LegacyApiKeyCredentialName))
        {
            return hasCurrentSecret;
        }

        var legacySecret = _credentialStore.GetSecretAsync(settings.Model.LegacyApiKeyCredentialName).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(legacySecret))
        {
            return false;
        }

        _credentialStore.SaveSecretAsync(ModelSettings.ApiKeySecretName, legacySecret).GetAwaiter().GetResult();
        return true;
    }

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsChatPage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    partial void OnComposerTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsComposerEmpty));
        SendCommand.NotifyCanExecuteChanged();
    }

    public void ShowCopyNotice(string message)
    {
        CopyNotice = message;
        IsCopyNoticeVisible = true;
        _copyNoticeCts?.Cancel();
        _copyNoticeCts?.Dispose();
        _copyNoticeCts = new CancellationTokenSource();
        var token = _copyNoticeCts.Token;
        _ = HideCopyNoticeAsync(token);
    }

    private async Task HideCopyNoticeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(2400, cancellationToken);
            IsCopyNoticeVisible = false;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer notice.
        }
    }
}
