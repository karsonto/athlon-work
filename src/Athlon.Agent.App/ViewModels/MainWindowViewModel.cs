using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.App.Services;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

using Athlon.Agent.App;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IFileStorageService _storage;
    private readonly ICredentialStore _credentialStore;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly IMcpRegistry _mcpRegistry;
    private readonly AppSettings _appSettings;
    private readonly IImpSsoSessionStore? _ssoSessionStore;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly ISkillRuntime _skillRuntime;
    private readonly IImageAttachmentReader _imageAttachmentReader;
    private readonly IImageAttachmentStore _imageAttachmentStore;
    private readonly IAppPathProvider _paths;
    private readonly SessionTurnHost _turnHost;
    private readonly QueuedTurnPresenter _queuedTurnPresenter;
    private readonly ComposerAtCompletionService _atCompletion;
    private readonly SessionUiCache _uiCache;
    private readonly ApplicationShutdownService _shutdownService;
    private readonly SchedulerService _scheduler;
    private readonly UiLayoutSettingsBridge _uiLayout;
    private readonly SessionHistoryCoordinator _sessionHistory;
    private readonly WorkspaceSessionBridge _workspaceBridge = new();
    private readonly ISessionUsageAccumulator _sessionUsageAccumulator;

    // In-memory caches to avoid re-reading session.json/conversation.jsonl on repeat clicks.
    private readonly Dictionary<string, AgentSession> _sessionCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ChatMessage>> _displayCache = new(StringComparer.Ordinal);

    private AgentSession _session = AgentSession.Create("New Chat");
    private string _displayedSessionId;
    private SessionTurnUiController _activeUi;

    public MainWindowViewModel(
        IFileStorageService storage,
        ICredentialStore credentialStore,
        IActiveWorkspaceContext workspaceContext,
        IMcpRegistry mcpRegistry,
        IImageAttachmentReader imageAttachmentReader,
        IImageAttachmentStore imageAttachmentStore,
        IAppPathProvider paths,
        IAgentSkillCatalog skillCatalog,
        ISkillRuntime skillRuntime,
        SessionTurnHost turnHost,
        QueuedTurnPresenter queuedTurnPresenter,
        ComposerAtCompletionService atCompletion,
        SessionUiCache uiCache,
        WorkspaceFileEditorService workspaceFileEditorService,
        ApplicationShutdownService shutdownService,
        AppSettings settings,
        IImpSsoSessionStore ssoSessionStore,
        ISessionUsageAccumulator sessionUsageAccumulator,
        SchedulerService scheduler)
    {
        _storage = storage;
        _credentialStore = credentialStore;
        _workspaceContext = workspaceContext;
        _mcpRegistry = mcpRegistry;
        _imageAttachmentReader = imageAttachmentReader;
        _imageAttachmentStore = imageAttachmentStore;
        _paths = paths;
        _turnHost = turnHost;
        _queuedTurnPresenter = queuedTurnPresenter;
        _queuedTurnPresenter.QueueChanged += OnQueuedTurnsChanged;
        _atCompletion = atCompletion;
        _uiCache = uiCache;
        _shutdownService = shutdownService;
        _scheduler = scheduler;
        _appSettings = settings;
        _ssoSessionStore = settings.Sso.Enabled ? ssoSessionStore : null;
        _uiLayout = new UiLayoutSettingsBridge(storage, settings);
        _sessionHistory = new SessionHistoryCoordinator(storage);
        _sessionUsageAccumulator = sessionUsageAccumulator;
        _skillCatalog = skillCatalog;
        _skillRuntime = skillRuntime;
        _displayedSessionId = _session.Id;
        _activeUi = _uiCache.GetOrCreate(_displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        WireSessionUsageUi(_activeUi);
        _activeUi.SetDisplayed(true);
        _turnHost.TurnCompleted += OnTurnCompleted;
        _turnHost.TurnStateChanged += OnTurnStateChanged;
        Settings = new SettingsViewModel(settings, _mcpRegistry, skillCatalog, paths);
        SchedulePageVm = new ScheduleViewModel(settings, storage, scheduler, OpenSessionByIdAsync);
        Settings.McpConfigurationChanged += async (_, _) => await RefreshMcpRuntimeAsync();
        Settings.SkillConfigurationChanged += (_, _) => OnSkillConfigurationChanged();
        Sidebar = new ContextSidebarViewModel(paths, skillCatalog, _mcpRegistry, settings);
        FileEditor = new FileEditorViewModel(workspaceFileEditorService);
        FileEditor.Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOpenEditorTabs));
        FileEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FileEditorViewModel.ActiveDocument) or nameof(FileEditorViewModel.HasOpenTabs))
            {
                OnPropertyChanged(nameof(HasOpenEditorTabs));
            }
        };
        HasStoredApiKey = EnsureCurrentApiKeySecret(settings);
        _uiLayout.ClampInitialLayout();

        LogsPath = paths.LogsPath;

        InitializeSsoDisplay();

        ApplySessionWorkspace();
        _activeUi.Messages.CollectionChanged += OnMessagesCollectionChanged;
        PendingImageAttachments.CollectionChanged += OnPendingImagesChanged;
        AppThemeManager.ThemeChanged += OnAppThemeChanged;
        _ = InitializeAsync();
    }

    public bool IsLightTheme => AppThemeManager.CurrentKind == AppThemeKind.Light;

    public string ThemeToggleGlyph => IsLightTheme ? "🌙" : "☀";

    public string ThemeToggleToolTip => IsLightTheme ? "切换到深色模式" : "切换到浅色模式";

    public bool HasChatMessages => Messages.Count > 0;

    private async Task RefreshMcpRuntimeAsync()
    {
        await _mcpRegistry.RefreshAsync(Settings.Settings.McpServers);
        Settings.RefreshRuntimeStates();
        Sidebar.Refresh(Settings.Settings);
        RefreshAtCompletionSources();
        OnPropertyChanged(nameof(Sidebar));
    }

    public async Task InitializeAsync()
    {
        await RefreshMcpRuntimeAsync();

        await RefreshSessionHistoryAsync();
        var latest = GetFirstAgentRecord();
        if (latest is not null && _session.Messages.Count == 0)
        {
            await LoadSessionInternalAsync(latest.Id);
        }
    }

    public ObservableCollection<ChatMessageViewModel> Messages => _activeUi.Messages;

    /// <summary>由 MainWindow 注入，用于流式输出时自动滚到底部（节流）。</summary>
    public Action? ScrollChatToBottom { get; set; }

    /// <summary>由 MainWindow 注入，用于非流式场景立即滚到底部。</summary>
    public Action? ScrollChatToBottomImmediate { get; set; }
    public ObservableCollection<AgentRecordGroupViewModel> AgentRecordGroups => _sessionHistory.AgentRecordGroups;
    public ObservableCollection<QueuedTurnViewModel> QueuedTurns => _queuedTurnPresenter.GetForSession(_displayedSessionId);
    public bool HasQueuedTurns => QueuedTurns.Count > 0;
    public ContextSidebarViewModel Sidebar { get; }
    public SettingsViewModel Settings { get; }
    public FileEditorViewModel FileEditor { get; }
    public string LogsPath { get; }

    public bool HasOpenEditorTabs => FileEditor.HasOpenTabs;

    public const double ContextSidebarMinWidth = UiLayoutConstraints.ContextSidebarMinWidth;
    public const double ContextSidebarMaxWidth = UiLayoutConstraints.ContextSidebarMaxWidth;
    public const double ContextSidebarDefaultWidth = UiLayoutConstraints.ContextSidebarDefaultWidth;

    public const double NavigationSidebarMinWidth = UiLayoutConstraints.NavigationSidebarMinWidth;
    public const double NavigationSidebarMaxWidth = UiLayoutConstraints.NavigationSidebarMaxWidth;
    public const double NavigationSidebarDefaultWidth = UiLayoutConstraints.NavigationSidebarDefaultWidth;

    public const double EditorPaneMinWidth = UiLayoutConstraints.EditorPaneMinWidth;
    public const double EditorPaneMaxWidth = UiLayoutConstraints.EditorPaneMaxWidth;
    public const double EditorPaneDefaultWidth = UiLayoutConstraints.EditorPaneDefaultWidth;

    public const double ComposerMinHeight = UiLayoutConstraints.ComposerMinHeight;
    public const double ComposerMaxHeight = UiLayoutConstraints.ComposerMaxHeight;
    public const double ComposerDefaultHeight = UiLayoutConstraints.ComposerDefaultHeight;

    public double EditorPaneWidth =>
        Math.Clamp(_appSettings.Ui.EditorPaneWidth, EditorPaneMinWidth, EditorPaneMaxWidth);

    public event EventHandler? ContextSidebarLayoutChanged;

    public double NavigationSidebarWidth =>
        Math.Clamp(_appSettings.Ui.NavigationSidebarWidth, NavigationSidebarMinWidth, NavigationSidebarMaxWidth);

    public double ComposerHeight =>
        Math.Clamp(_appSettings.Ui.ComposerHeight, ComposerMinHeight, ComposerMaxHeight);

    public bool IsContextSidebarVisible => _appSettings.Ui.ContextSidebarVisible;

    public double ContextSidebarWidth =>
        Math.Clamp(_appSettings.Ui.ContextSidebarWidth, ContextSidebarMinWidth, ContextSidebarMaxWidth);

    public string ContextSidebarToggleToolTip =>
        IsContextSidebarVisible ? "关闭右侧栏 (Ctrl+Alt+B)" : "打开右侧栏 (Ctrl+Alt+B)";

    public const double ContextSidebarCollapseDragThreshold = UiLayoutConstraints.ContextSidebarCollapseDragThreshold;

    private CancellationTokenSource? _copyNoticeCts;

    [ObservableProperty]
    private string copyNotice = string.Empty;

    [ObservableProperty]
    private bool isCopyNoticeVisible;

    [ObservableProperty]
    private string composerText = string.Empty;

    public bool IsComposerEmpty => string.IsNullOrWhiteSpace(ComposerText);

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isLoadingSession;

    [ObservableProperty]
    private string shutdownStatusText = "正在关闭…";

    [ObservableProperty]
    private string currentPage = "Chat";

    [ObservableProperty]
    private string currentSessionTitle = "New Chat";

    [ObservableProperty]
    private string sessionUsageLine = string.Empty;

    [ObservableProperty]
    private string settingsStatus = "Settings are stored as JSON files under the app data folder.";

    [ObservableProperty]
    private string apiKey = string.Empty;

    [ObservableProperty]
    private bool hasStoredApiKey;

    [ObservableProperty]
    private string activeWorkspaceName = "No workspace";

    [ObservableProperty]
    private bool isAtCompletionOpen;

    [ObservableProperty]
    private int selectedAtCompletionIndex = -1;

    [ObservableProperty]
    private string ssoDisplayName = string.Empty;

    [ObservableProperty]
    private bool isSsoUserVisible;

    public ObservableCollection<AtCompletionItemViewModel> AtCompletionItems { get; } = new();
    public ObservableCollection<PendingImageAttachmentViewModel> PendingImageAttachments { get; } = new();
    public bool HasPendingImages => PendingImageAttachments.Count > 0;

    public bool IsChatPage => CurrentPage == "Chat";
    public bool IsSettingsPage => CurrentPage == "Settings";
    public bool IsSchedulePage => CurrentPage == "Schedule";

    public ScheduleViewModel SchedulePageVm { get; }

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = page;
        if (string.Equals(page, "Settings", StringComparison.Ordinal))
        {
            Settings.SyncSkillsFromCatalog();
        }
    }

    [RelayCommand]
    private void SsoLogout()
    {
        if (!_appSettings.Sso.Enabled)
        {
            return;
        }

        var result = MessageBox.Show(
            "确定要退出登录吗？",
            AppVersionInfo.ProductName,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _ssoSessionStore?.Clear();
        Application.Current.Shutdown(0);
    }

    private void InitializeSsoDisplay()
    {
        if (!_appSettings.Sso.Enabled || _ssoSessionStore is null)
        {
            SsoDisplayName = string.Empty;
            IsSsoUserVisible = false;
            return;
        }

        var session = _ssoSessionStore.GetCachedSession();
        if (session is null || _ssoSessionStore.IsExpired(session))
        {
            SsoDisplayName = string.Empty;
            IsSsoUserVisible = false;
            return;
        }

        SsoDisplayName = session.DisplayName;
        IsSsoUserVisible = !string.IsNullOrWhiteSpace(SsoDisplayName);
    }

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        var next = AppThemeManager.CurrentKind == AppThemeKind.Light
            ? AppThemeKind.Dark
            : AppThemeKind.Light;
        AppThemeManager.SetTheme(next, _appSettings.Ui);
        NotifyThemeToggleStateChanged();
        await _uiLayout.PersistNowAsync();
    }

    [RelayCommand]
    private async Task ToggleContextSidebarAsync()
    {
        SetContextSidebarVisible(!_appSettings.Ui.ContextSidebarVisible);
        await _uiLayout.PersistNowAsync();
    }

    private void OnAppThemeChanged(object? sender, EventArgs e) => NotifyThemeToggleStateChanged();

    private void NotifyThemeToggleStateChanged()
    {
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(ThemeToggleGlyph));
        OnPropertyChanged(nameof(ThemeToggleToolTip));
    }

    public void SetContextSidebarVisible(bool visible)
    {
        var ui = _appSettings.Ui;
        if (ui.ContextSidebarVisible == visible)
        {
            return;
        }

        ui.ContextSidebarVisible = visible;
        if (visible && ui.ContextSidebarWidth < ContextSidebarMinWidth)
        {
            ui.ContextSidebarWidth = ContextSidebarDefaultWidth;
        }

        NotifyContextSidebarLayoutChanged();
    }

    public void UpdateContextSidebarWidth(double width)
    {
        if (!_appSettings.Ui.ContextSidebarVisible)
        {
            return;
        }

        _uiLayout.TryUpdateDimension(
            _appSettings.Ui.ContextSidebarWidth,
            width,
            ContextSidebarMinWidth,
            ContextSidebarMaxWidth,
            value => _appSettings.Ui.ContextSidebarWidth = value);
    }

    public void UpdateNavigationSidebarWidth(double width) =>
        _uiLayout.TryUpdateDimension(
            _appSettings.Ui.NavigationSidebarWidth,
            width,
            NavigationSidebarMinWidth,
            NavigationSidebarMaxWidth,
            value => _appSettings.Ui.NavigationSidebarWidth = value);

    public void UpdateComposerHeight(double height) =>
        _uiLayout.TryUpdateDimension(
            _appSettings.Ui.ComposerHeight,
            height,
            ComposerMinHeight,
            ComposerMaxHeight,
            value => _appSettings.Ui.ComposerHeight = value);

    private void NotifyContextSidebarLayoutChanged()
    {
        OnPropertyChanged(nameof(IsContextSidebarVisible));
        OnPropertyChanged(nameof(ContextSidebarWidth));
        OnPropertyChanged(nameof(ContextSidebarToggleToolTip));
        ContextSidebarLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task PersistUiLayoutForSidebarAsync() => _uiLayout.PersistNowAsync();

    [RelayCommand]
    private Task NewSession()
    {
        var previousSession = _session;
        _session = AgentSession.Create("New Chat");
        SwitchDisplayedSession(_session);
        CurrentSessionTitle = _session.Title;
        ComposerText = string.Empty;
        PendingImageAttachments.Clear();
        UpdateDisplayedBusyState();
        CurrentPage = "Chat";
        ApplySessionWorkspace();
        _ = SaveSessionInBackgroundAsync(previousSession);
        RequestRefreshSessionHistory();
        NotifyCommandStatesChanged();
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanClearContext))]
    private async Task ClearContextAsync()
    {
        var confirm = MessageBox.Show(
            "将清空当前对话在模型中的全部可见历史（用户、助手、工具与压缩记录）。\n\n会话 ID、工作区与标题会保留；磁盘上的 transcript 归档不会删除。\n\n下次发送消息时会重新构建系统提示（工作区、工具、技能等）。",
            "清空上下文",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (_turnHost.IsRunning(_displayedSessionId))
        {
            _turnHost.Cancel(_displayedSessionId);
        }

        _session = _session.WithMessages(Array.Empty<ChatMessage>());
        _activeUi.Messages.Clear();
        await _storage.ClearConversationDisplayAsync(_session.Id);
        PendingImageAttachments.Clear();

        await _storage.SaveSessionAsync(_session);
        InvalidateSessionCache(_session.Id);
        SettingsStatus = "上下文已清空。";
        NotifyCommandStatesChanged();
    }

    private bool CanClearContext() => Messages.Count > 0 && !IsBusy;

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasChatMessages));
        ClearContextCommand.NotifyCanExecuteChanged();

        if (IsBusy && e.Action == NotifyCollectionChangedAction.Add)
        {
            ScrollChatToBottom?.Invoke();
        }
    }

    private void OnPendingImagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingImages));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCommandStatesChanged()
    {
        SendCommand.NotifyCanExecuteChanged();
        ClearContextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasChatMessages));
    }

    [RelayCommand]
    private async Task LoadSessionAsync(SessionHistoryItemViewModel? item)
    {
        if (item is null || item.Id == _session.Id)
        {
            return;
        }

        var previousSession = _session;
        await LoadSessionInternalAsync(item.Id);
        _ = SaveSessionInBackgroundAsync(previousSession);
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

        if (_turnHost.IsRunning(item.Id))
        {
            _turnHost.Cancel(item.Id);
        }

        _turnHost.ClearQueue(item.Id);
        _queuedTurnPresenter.RemoveSession(item.Id);

        _uiCache.Remove(item.Id);
        await _storage.DeleteSessionAsync(item.Id);
        InvalidateSessionCache(item.Id);

        if (string.Equals(_session.Id, item.Id, StringComparison.Ordinal))
        {
            _session = AgentSession.Create("New Chat");
            SwitchDisplayedSession(_session);
            CurrentSessionTitle = _session.Title;
            ComposerText = string.Empty;
            PendingImageAttachments.Clear();
            ApplySessionWorkspace();
            CurrentPage = "Chat";
        }

        await RefreshSessionHistoryAsync();
        SettingsStatus = "对话已删除。";
        NotifyCommandStatesChanged();
    }

    [RelayCommand]
    private async Task SelectImagesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Multiselect = true,
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.gif"
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var images = await _imageAttachmentReader.ReadImagesAsync(dialog.FileNames);
        AddPendingImages(images);
    }

    public void AddPendingImages(IEnumerable<ImageAttachment> images)
    {
        foreach (var image in images)
        {
            if (PendingImageAttachments.Any(existing => ImageAttachmentsMatch(existing.Attachment, image)))
            {
                continue;
            }

            PendingImageAttachments.Add(new PendingImageAttachmentViewModel(image));
        }
    }

    [RelayCommand]
    private void RemovePendingImage(PendingImageAttachmentViewModel? image)
    {
        if (image is null)
        {
            return;
        }

        PendingImageAttachments.Remove(image);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        CloseAtCompletion();

        if (string.IsNullOrWhiteSpace(ComposerText) && PendingImageAttachments.Count == 0)
        {
            return;
        }

        _skillCatalog.Reload();
        var input = SkillComposerExpander.Expand(ComposerText, _skillRuntime.GetSkills());
        var imageAttachments = PersistPendingImages(_displayedSessionId);
        ComposerText = string.Empty;
        SyncWorkspaceContext();

        var ui = _uiCache.GetOrCreate(_displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        PendingImageAttachments.Clear();

        if (_turnHost.IsRunning(_displayedSessionId))
        {
            var queueId = Guid.NewGuid().ToString("N");
            _queuedTurnPresenter.Enqueue(_displayedSessionId, queueId, input, imageAttachments, ui);
            SettingsStatus = "已加入排队";
            NotifyCommandStatesChanged();
            return;
        }

        ui.AddUserMessage(input, imageAttachments);
        var request = new SessionTurnRequest(_displayedSessionId, _session, input, imageAttachments, ui, IsAutoContinue: false);
        if (!_turnHost.TryStart(request, out var error))
        {
            SettingsStatus = error ?? "无法开始生成。";
            NotifyCommandStatesChanged();
            return;
        }

        UpdateDisplayedBusyState();
        NotifyCommandStatesChanged();
    }

    [RelayCommand]
    private void RemoveQueuedTurn(QueuedTurnViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _queuedTurnPresenter.Remove(_displayedSessionId, item.QueueId);
    }

    private void RequestScrollToBottom() => ScrollChatToBottom?.Invoke();

    private void RequestScrollToBottomImmediate() => ScrollChatToBottomImmediate?.Invoke();

    private void WireSessionUsageUi(SessionTurnUiController ui)
    {
        ui.OnUsageRecorded = snapshot => SessionUsageLine = SessionUsageFormatter.Format(snapshot);
        SessionUsageLine = SessionUsageFormatter.Format(_sessionUsageAccumulator.Get(_displayedSessionId));
    }

    private void SwitchDisplayedSession(AgentSession session)
    {
        _activeUi.SetDisplayed(false);
        _activeUi.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        _displayedSessionId = session.Id;
        _session = session;
        _activeUi = _uiCache.GetOrCreate(_displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        WireSessionUsageUi(_activeUi);
        _activeUi.SetDisplayed(true);
        _activeUi.Messages.CollectionChanged += OnMessagesCollectionChanged;
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(QueuedTurns));
        OnPropertyChanged(nameof(HasQueuedTurns));
        UpdateDisplayedBusyState();
    }

    private void OnTurnStateChanged(object? sender, string sessionId)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
            {
                UpdateDisplayedBusyState();
            }

            RequestRefreshSessionHistory();
        });
    }

    private void OnTurnCompleted(object? sender, SessionTurnCompletedEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            if (string.Equals(e.SessionId, _displayedSessionId, StringComparison.Ordinal))
            {
                _session = e.Session;
                CurrentSessionTitle = _session.Title;
                UpdateDisplayedBusyState();
            }

            await SaveSessionCoreAsync(
                string.Equals(e.SessionId, _displayedSessionId, StringComparison.Ordinal)
                    ? _session
                    : e.Session);
            RequestRefreshSessionHistory();
            if (_queuedTurnPresenter.TryProcessNext(e, out var queueError))
            {
                if (string.Equals(e.SessionId, _displayedSessionId, StringComparison.Ordinal))
                {
                    UpdateDisplayedBusyState();
                    if (queueError is not null)
                    {
                        SettingsStatus = queueError;
                    }
                }
            }

            NotifyCommandStatesChanged();
        });
    }

    private void UpdateDisplayedBusyState()
    {
        IsBusy = _turnHost.IsRunning(_displayedSessionId);
    }

    private void StopSession(string sessionId)
    {
        _turnHost.Cancel(sessionId);
        _queuedTurnPresenter.Clear(sessionId);
    }

    [RelayCommand]
    private void Stop() => StopSession(_displayedSessionId);

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
        Settings.SyncSkillsFromCatalog();
        await _storage.SaveSettingsAsync(Settings.Settings);
        await RefreshMcpRuntimeAsync();
        ApplySessionWorkspace();
        OnPropertyChanged(nameof(Sidebar));
        SettingsStatus = $"Saved at {AppTimeZone.Now:HH:mm:ss}";
        CurrentPage = "Chat";
    }

    public Task OpenWorkspaceFileInEditorAsync(string path) =>
        FileEditor.OpenFileAsync(path, _session.ActiveWorkspace);

    [RelayCommand(CanExecute = nameof(CanOpenWorkspaceTreeNodeInEditor))]
    private async Task OpenWorkspaceTreeNodeInEditorAsync(WorkspaceTreeNodeViewModel? node)
    {
        if (!CanOpenWorkspaceTreeNodeInEditor(node) || node is null || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        await OpenWorkspaceFileInEditorAsync(node.FullPath).ConfigureAwait(true);
    }

    private bool CanOpenWorkspaceTreeNodeInEditor(WorkspaceTreeNodeViewModel? node) =>
        node is not null
        && !node.IsPlaceholder
        && !node.IsExpanderPlaceholder
        && !node.IsDirectory
        && !string.IsNullOrWhiteSpace(node.FullPath);

    [RelayCommand]
    private async Task SaveActiveEditorAsync()
    {
        if (FileEditor.ActiveDocument is null)
        {
            return;
        }

        await FileEditor.SaveDocumentAsync(FileEditor.ActiveDocument).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseEditorTab(EditorDocumentViewModel? document) =>
        FileEditor.CloseTabCommand.Execute(document);

    public bool ConfirmCloseEditorTabs() => FileEditor.TryCloseAllTabs();

    public void UpdateEditorPaneWidth(double width) =>
        _uiLayout.TryUpdateDimension(
            _appSettings.Ui.EditorPaneWidth,
            width,
            EditorPaneMinWidth,
            EditorPaneMaxWidth,
            value => _appSettings.Ui.EditorPaneWidth = value);

    [RelayCommand(CanExecute = nameof(CanDeleteWorkspaceItem))]
    private void DeleteWorkspaceItem(WorkspaceTreeNodeViewModel? node)
    {
        if (!CanDeleteWorkspaceItem(node) || node is null || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        var path = Path.GetFullPath(node.FullPath);
        var kind = node.IsDirectory ? "文件夹" : "文件";
        var prompt = node.IsDirectory
            ? $"确定删除{kind}「{node.Name}」及其全部内容吗？此操作无法撤销。"
            : $"确定删除{kind}「{node.Name}」吗？此操作无法撤销。";

        if (MessageBox.Show(prompt, "删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (node.IsDirectory)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
            else
            {
                SettingsStatus = "目标不存在或已被删除。";
                Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
                return;
            }

            RefreshAtCompletionSources();
            Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
            SettingsStatus = $"已删除{kind}「{node.Name}」。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法删除「{node.Name}」：{exception.Message}",
                "删除失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SettingsStatus = $"删除失败：{exception.Message}";
        }
    }

    private bool CanDeleteWorkspaceItem(WorkspaceTreeNodeViewModel? node) =>
        node is not null
        && !node.IsPlaceholder
        && !node.IsExpanderPlaceholder
        && !string.IsNullOrWhiteSpace(node.FullPath)
        && WorkspaceSessionBridge.TryGetActiveWorkspaceRoot(_session, out var root)
        && WorkspaceSessionBridge.IsPathUnderWorkspace(root, node.FullPath)
        && !WorkspaceSessionBridge.IsWorkspaceRootPath(root, node.FullPath);

    private bool CanSend() =>
        !string.IsNullOrWhiteSpace(ComposerText) || PendingImageAttachments.Count > 0;

    private void OnQueuedTurnsChanged(string sessionId)
    {
        if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(QueuedTurns));
            OnPropertyChanged(nameof(HasQueuedTurns));
        }
    }

    public void UpdateComposerCompletion(string composerText, int caretIndex) =>
        UpdateAtCompletion(composerText, caretIndex);

    public void UpdateAtCompletion(string composerText, int caretIndex)
    {
        if (!ComposerAtCompletionService.TryGetQuery(composerText, caretIndex, out var query))
        {
            CloseAtCompletion();
            return;
        }

        _atCompletion.EnsureFileIndexBuilt(
            _skillCatalog,
            _session.ActiveWorkspace,
            _workspaceContext.IgnorePatterns);

        var sorted = _atCompletion.FilterMatches(query);

        AtCompletionItems.Clear();
        foreach (var item in sorted)
        {
            AtCompletionItems.Add(item);
        }

        if (AtCompletionItems.Count == 0)
        {
            CloseAtCompletion();
            return;
        }

        IsAtCompletionOpen = true;
        if (SelectedAtCompletionIndex < 0 || SelectedAtCompletionIndex >= AtCompletionItems.Count)
        {
            SelectedAtCompletionIndex = 0;
        }
    }

    public void MoveAtCompletionSelection(int delta)
    {
        if (!IsAtCompletionOpen || AtCompletionItems.Count == 0)
        {
            return;
        }

        var next = SelectedAtCompletionIndex + delta;
        if (next < 0)
        {
            next = AtCompletionItems.Count - 1;
        }
        else if (next >= AtCompletionItems.Count)
        {
            next = 0;
        }

        SelectedAtCompletionIndex = next;
    }

    public bool TryAcceptAtCompletion(int caretIndex, out int newCaretIndex)
    {
        newCaretIndex = caretIndex;
        if (!IsAtCompletionOpen
            || SelectedAtCompletionIndex < 0
            || SelectedAtCompletionIndex >= AtCompletionItems.Count
            || !ComposerCompletionQuery.TryGetAtQuerySpan(ComposerText, caretIndex, out var atStart, out var atEndExclusive))
        {
            return false;
        }

        var replacement = ComposerAtCompletionService.FormatReplacement(AtCompletionItems[SelectedAtCompletionIndex]);
        ComposerText = ComposerText[..atStart] + replacement + ComposerText[atEndExclusive..];
        newCaretIndex = atStart + replacement.Length;
        CloseAtCompletion();
        return true;
    }

    public void CloseAtCompletion()
    {
        IsAtCompletionOpen = false;
        SelectedAtCompletionIndex = -1;
        AtCompletionItems.Clear();
    }

    private void ApplySessionWorkspace()
    {
        SyncWorkspaceContext();
        ActiveWorkspaceName = string.IsNullOrWhiteSpace(_workspaceContext.DisplayName) ? "未配置工作区" : _workspaceContext.DisplayName!;
        RefreshAtCompletionSources(reloadSkills: true);
        Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
        ConfigureWorkspaceWatcher();
        OnPropertyChanged(nameof(Sidebar));
    }

    private void SyncWorkspaceContext() =>
        _workspaceBridge.SyncWorkspaceContext(_session, _appSettings, _workspaceContext);

    private async Task SaveCurrentSessionIfNeededAsync() =>
        await SaveCurrentSessionIfNeededAsync(_session);

    private async Task SaveCurrentSessionIfNeededAsync(AgentSession session)
    {
        await SaveSessionCoreAsync(session);
        await RefreshSessionHistoryAsync();
    }

    private async Task SaveSessionCoreAsync(AgentSession session)
    {
        if (session.Messages.Count == 0)
        {
            return;
        }

        var toSave = SessionHistoryCoordinator.DeriveSessionTitle(session);
        if (string.Equals(toSave.Id, _displayedSessionId, StringComparison.Ordinal))
        {
            _session = toSave;
        }

        await _storage.SaveSessionAsync(toSave);
        InvalidateSessionCache(toSave.Id);
    }

    private async Task SaveSessionInBackgroundAsync(AgentSession session)
    {
        try
        {
            await SaveSessionCoreAsync(session);
            RequestRefreshSessionHistory();
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
                SettingsStatus = $"保存对话失败：{ex.Message}");
        }
    }

    private void RequestRefreshSessionHistory() =>
        _sessionHistory.RequestRefresh(RefreshSessionHistoryAsync);

    private async Task RefreshSessionHistoryAsync()
    {
        await _sessionHistory.RefreshAsync(_session.Id, _turnHost.IsRunning, StopSession);
        OnPropertyChanged(nameof(HasAgentRecords));
    }

    public bool HasAgentRecords => _sessionHistory.HasAgentRecords;

    private SessionHistoryItemViewModel? GetFirstAgentRecord() => _sessionHistory.GetFirstAgentRecord();

    public async Task OpenSessionByIdAsync(string sessionId)
    {
        CurrentPage = "Chat";
        await LoadSessionInternalAsync(sessionId);
    }

    private async Task LoadSessionInternalAsync(string sessionId)
    {
        IsLoadingSession = true;
        SettingsStatus = "正在加载对话…";
        try
        {
            if (!_sessionCache.TryGetValue(sessionId, out var loaded))
            {
                loaded = await _storage.LoadSessionAsync(sessionId);
                if (loaded is null)
                {
                    SettingsStatus = "无法加载该对话。";
                    return;
                }

                _sessionCache[sessionId] = loaded;
            }

            SwitchDisplayedSession(loaded);
            CurrentSessionTitle = _session.Title;
            ComposerText = string.Empty;
            PendingImageAttachments.Clear();

            // conversation.jsonl is the display source of truth; session.json may be saved
            // before the assistant reply (e.g. scheduled tasks) and only contain the user turn.
            if (!_displayCache.TryGetValue(sessionId, out var displayMessages))
            {
                displayMessages = await _storage.LoadConversationDisplayAsync(sessionId);
                _displayCache[sessionId] = displayMessages;
            }

            if (displayMessages.Count > 0)
            {
                await _activeUi.HydrateDisplayAsync(_session, displayMessages);
            }
            else if (_session.Messages.Count > 0)
            {
                await _activeUi.HydrateFromSessionAsync(_session);
            }
            else
            {
                await _activeUi.HydrateFromSessionAsync(_session);
            }

            ApplySessionWorkspace();
            UpdateDisplayedBusyState();
            SettingsStatus = $"已加载对话：{_session.Title}";
        }
        finally
        {
            IsLoadingSession = false;
            NotifyCommandStatesChanged();
        }
    }

    private void InvalidateSessionCache(string sessionId)
    {
        _sessionCache.Remove(sessionId);
        _displayCache.Remove(sessionId);
    }

    private void ConfigureWorkspaceWatcher() =>
        _workspaceBridge.ConfigureWatcher(
            _session,
            path => FileEditor.HandleExternalFileChange(path),
            () =>
            {
                RefreshAtCompletionSources();
                Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
            });

    public bool HasPendingShutdownWork => _turnHost.HasActiveWork;

    public async Task ShutdownAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _workspaceBridge.Dispose();

        await _shutdownService.ShutdownAsync(progress, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        _turnHost.TurnCompleted -= OnTurnCompleted;
        _turnHost.TurnStateChanged -= OnTurnStateChanged;
        ShutdownAsync().GetAwaiter().GetResult();
        _activeUi.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        _copyNoticeCts?.Cancel();
        _copyNoticeCts?.Dispose();
        _uiLayout.Dispose();
        _sessionHistory.Dispose();
        _workspaceBridge.Dispose();
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
        OnPropertyChanged(nameof(IsSchedulePage));
        if (string.Equals(value, "Settings", StringComparison.Ordinal))
        {
            Settings.SyncSkillsFromCatalog();
        }
        else if (string.Equals(value, "Schedule", StringComparison.Ordinal))
        {
            SchedulePageVm.SyncFromSettings();
        }
    }

    private void OnSkillConfigurationChanged()
    {
        _skillCatalog.Reload();
        Sidebar.Refresh(_appSettings);
        RefreshAtCompletionSources(reloadSkills: true);
        OnPropertyChanged(nameof(Sidebar));
    }

    partial void OnIsBusyChanged(bool value)
    {
        ClearContextCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnComposerTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsComposerEmpty));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void RefreshAtCompletionSources(bool reloadSkills = false) =>
        _atCompletion.RefreshSources(
            _skillCatalog,
            _session.ActiveWorkspace,
            _workspaceContext.IgnorePatterns,
            reloadSkills);

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

    private ImageAttachment[] PersistPendingImages(string sessionId)
    {
        var persisted = new List<ImageAttachment>(PendingImageAttachments.Count);
        foreach (var pending in PendingImageAttachments)
        {
            persisted.Add(PersistImageAttachment(sessionId, pending.Attachment));
        }

        return persisted.ToArray();
    }

    private ImageAttachment PersistImageAttachment(string sessionId, ImageAttachment attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.DataUrl))
        {
            return attachment;
        }

        if (string.IsNullOrWhiteSpace(attachment.LocalPath))
        {
            return attachment;
        }

        var sessionAttachmentsRoot = Path.Combine(_paths.SessionsPath, sessionId, "attachments");
        if (attachment.LocalPath.StartsWith(sessionAttachmentsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return attachment;
        }

        return _imageAttachmentStore.SaveFromFile(sessionId, attachment.LocalPath);
    }

    private static bool ImageAttachmentsMatch(ImageAttachment left, ImageAttachment right) =>
        (!string.IsNullOrWhiteSpace(left.LocalPath)
            && string.Equals(left.LocalPath, right.LocalPath, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(left.DataUrl)
            && string.Equals(left.DataUrl, right.DataUrl, StringComparison.Ordinal));
}

public sealed record AtCompletionItemViewModel(
    string Type,
    string PrimaryText,
    string SecondaryText,
    string InsertText,
    string MatchText);

public sealed class PendingImageAttachmentViewModel
{
    public PendingImageAttachmentViewModel(ImageAttachment attachment)
    {
        Attachment = attachment;
    }

    public ImageAttachment Attachment { get; }
    public string FileName => Attachment.FileName;
    public string MimeType => Attachment.MimeType;
    public System.Windows.Media.ImageSource? Thumbnail =>
        Services.ImageAttachmentUi.TryCreateThumbnail(Attachment);
}
