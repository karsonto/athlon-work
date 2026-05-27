using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IFileStorageService _storage;
    private readonly ICredentialStore _credentialStore;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly IMcpRegistry _mcpRegistry;
    private readonly AppSettings _appSettings;
    private FileSystemWatcher? _workspaceWatcher;
    private CancellationTokenSource? _turnCancellation;
    private AgentSession _session = AgentSession.Create("New Chat");
    private ChatMessageViewModel? _streamingAssistantMessage;
    private readonly StringBuilder _streamingTokenBuffer = new();
    private DispatcherTimer? _streamingFlushTimer;

    public MainWindowViewModel(
        IAgentOrchestrator orchestrator,
        IFileStorageService storage,
        ICredentialStore credentialStore,
        IActiveWorkspaceContext workspaceContext,
        IMcpRegistry mcpRegistry,
        IAppPathProvider paths,
        IAgentSkillCatalog skillCatalog,
        AppSettings settings)
    {
        _orchestrator = orchestrator;
        _storage = storage;
        _credentialStore = credentialStore;
        _workspaceContext = workspaceContext;
        _mcpRegistry = mcpRegistry;
        _appSettings = settings;
        Settings = new SettingsViewModel(settings, _mcpRegistry);
        Settings.McpConfigurationChanged += async (_, _) => await RefreshMcpRuntimeAsync();
        Sidebar = new ContextSidebarViewModel(paths, skillCatalog, _mcpRegistry, settings);
        HasStoredApiKey = EnsureCurrentApiKeySecret(settings);
        ApplySessionWorkspace();
        _ = InitializeAsync();
    }

    private async Task RefreshMcpRuntimeAsync()
    {
        await _mcpRegistry.RefreshAsync(Settings.Settings.McpServers);
        Settings.RefreshRuntimeStates();
        Sidebar.Refresh(Settings.Settings);
        OnPropertyChanged(nameof(Sidebar));
    }

    public async Task InitializeAsync()
    {
        await RefreshMcpRuntimeAsync();

        await RefreshSessionHistoryAsync();
        var latest = SessionHistory.FirstOrDefault();
        if (latest is not null && _session.Messages.Count == 0)
        {
            await LoadSessionInternalAsync(latest.Id);
        }
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    /// <summary>由 MainWindow 注入，用于流式输出时自动滚到底部。</summary>
    public Action? ScrollChatToBottom { get; set; }
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
        RequestScrollToBottom();
        SyncWorkspaceContext();
        await Task.Yield();
        EnsureStreamingAssistantMessage();

        try
        {
            var callbacks = new AgentTurnCallbacks
            {
                OnToolStarted = async toolCall => await RunOnUiAsync(() =>
                {
                    ClearStreamingAssistantPlaceholder();
                    if (FindToolMessage(toolCall.Id) is not null)
                    {
                        return;
                    }

                    Messages.Add(ChatMessageViewModel.CreatePendingTool(toolCall));
                    RequestScrollToBottom();
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
                        RequestScrollToBottom();
                        return;
                    }

                    if (message.Role == MessageRole.Assistant && _streamingAssistantMessage is not null)
                    {
                        FlushStreamingTokens();
                        _streamingAssistantMessage.CompleteStreamingAssistant(message);
                        _streamingAssistantMessage = null;
                        RequestScrollToBottom();
                        return;
                    }

                    ClearStreamingAssistantPlaceholder();
                    Messages.Add(new ChatMessageViewModel(message));
                    RequestScrollToBottom();
                }),
                OnAssistantTextDelta = async token => await RunOnUiAsync(() =>
                {
                    EnsureStreamingAssistantMessage();
                    _streamingTokenBuffer.Append(token);
                    ScheduleStreamingFlush();
                })
            };

            _session = await Task.Run(
                async () => await _orchestrator.SendAsync(_session, input, callbacks, _turnCancellation.Token).ConfigureAwait(false),
                _turnCancellation.Token).ConfigureAwait(true);
            CurrentSessionTitle = _session.Title;
            await SaveCurrentSessionIfNeededAsync();
        }
        catch (OperationCanceledException)
        {
            await RunOnUiAsync(() =>
            {
                FlushStreamingTokens();
                if (_streamingAssistantMessage is not null)
                {
                    _streamingAssistantMessage.MarkStreamingCancelled();
                    _streamingAssistantMessage = null;
                }

                foreach (var message in Messages.Where(static message => message.IsToolRunning))
                {
                    message.MarkToolCancelled();
                }

                Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.System, "生成已停止。")));
            });

            var reloaded = await _storage.LoadSessionAsync(_session.Id);
            if (reloaded is not null)
            {
                _session = reloaded;
            }

            await SaveCurrentSessionIfNeededAsync();
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                ClearStreamingAssistantPlaceholder();
                Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.System, $"模型调用失败：{ex.Message}")));
            });
            await SaveCurrentSessionIfNeededAsync();
        }
        finally
        {
            StopStreamingFlushTimer();
            FlushStreamingTokens();
            _streamingAssistantMessage = null;
            StreamingText = string.Empty;
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private void EnsureStreamingAssistantMessage()
    {
        if (_streamingAssistantMessage is not null)
        {
            return;
        }

        _streamingAssistantMessage = ChatMessageViewModel.CreateStreamingAssistant();
        Messages.Add(_streamingAssistantMessage);
        RequestScrollToBottom();
    }

    private void ClearStreamingAssistantPlaceholder()
    {
        StopStreamingFlushTimer();
        _streamingTokenBuffer.Clear();

        if (_streamingAssistantMessage is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_streamingAssistantMessage.Content))
        {
            Messages.Remove(_streamingAssistantMessage);
        }

        _streamingAssistantMessage = null;
    }

    private void ScheduleStreamingFlush()
    {
        if (_streamingFlushTimer is not null)
        {
            return;
        }

        _streamingFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _streamingFlushTimer.Tick += (_, _) => FlushStreamingTokens();
        _streamingFlushTimer.Start();
    }

    private void FlushStreamingTokens()
    {
        if (_streamingTokenBuffer.Length == 0 || _streamingAssistantMessage is null)
        {
            return;
        }

        _streamingAssistantMessage.AppendStreamingToken(_streamingTokenBuffer.ToString());
        _streamingTokenBuffer.Clear();
        RequestScrollToBottom();
    }

    private void RequestScrollToBottom() => ScrollChatToBottom?.Invoke();

    private void StopStreamingFlushTimer()
    {
        if (_streamingFlushTimer is null)
        {
            return;
        }

        _streamingFlushTimer.Stop();
        _streamingFlushTimer = null;
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
        SettingsViewModel.PruneEmptyWorkspaces(Settings.Settings);
        await _storage.SaveSettingsAsync(Settings.Settings);
        await RefreshMcpRuntimeAsync();
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
        if (!string.IsNullOrWhiteSpace(_session.ActiveWorkspace))
        {
            _workspaceContext.SetWorkspace(_session.ActiveWorkspace);
            return;
        }

        var configured = _appSettings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
        if (configured is null)
        {
            _workspaceContext.SetWorkspace(null);
            return;
        }

        _workspaceContext.SetWorkspace(configured.RootPath, configured.Name, configured.IgnorePatterns);
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

    /// <summary>Cancel in-flight work and release watchers before process exit.</summary>
    public void PrepareForShutdown()
    {
        try { _turnCancellation?.Cancel(); } catch { /* ignore */ }
        _workspaceWatcher?.Dispose();
        _workspaceWatcher = null;
        StopStreamingFlushTimer();
    }

    public void Dispose()
    {
        PrepareForShutdown();
        _turnCancellation?.Dispose();
        _copyNoticeCts?.Cancel();
        _copyNoticeCts?.Dispose();
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
