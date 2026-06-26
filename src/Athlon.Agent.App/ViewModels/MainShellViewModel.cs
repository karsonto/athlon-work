using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.App.Services;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

using System.Windows.Controls;
using Athlon.Agent.App.Navigation;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.App.ViewModels;

public partial class MainShellViewModel : ObservableObject, IDisposable, ISessionHost, INavigationService
{
    private readonly IFileStorageService _storage;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly IMcpRegistry _mcpRegistry;
    private readonly AppSettings _appSettings;
    private readonly IImpSsoSessionStore? _ssoSessionStore;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly SessionTurnCoordinator _sessionTurns;
    private readonly ComposerCoordinator _composer;
    private readonly LayoutCoordinator _layout;
    private readonly NavigationCoordinator _navigation;
    private readonly IChatScrollService _chatScroll;
    private readonly SessionUiCache _uiCache;
    private readonly ApplicationShutdownService _shutdownService;
    private readonly SessionHistoryCoordinator _sessionHistory;
    private readonly SessionNavigationStore _sessionNavigation;
    private readonly WorkspaceSessionBridge _workspaceBridge = new();
    private readonly ISessionUsageAccumulator _sessionUsageAccumulator;
    private readonly PageViewFactory _pageViewFactory;
    private readonly ITaskListChangedNotifier _taskListChangedNotifier;

    private AgentSession _session = AgentSession.Create("New Chat");
    private string _displayedSessionId;
    private SessionTurnUiController _activeUi;
    private bool _shutdownCompleted;
    private bool _disposed;
    private int _sessionLoadGeneration;
    private int _composerCaretIndex;
    private Controls.WebChatView? _savedChatView;

    public MainShellViewModel(
        IFileStorageService storage,
        IActiveWorkspaceContext workspaceContext,
        IMcpRegistry mcpRegistry,
        IAppPathProvider paths,
        IAgentSkillCatalog skillCatalog,
        SessionTurnCoordinator sessionTurns,
        ComposerCoordinator composer,
        LayoutCoordinator layout,
        NavigationCoordinator navigation,
        IChatScrollService chatScroll,
        SessionUiCache uiCache,
        ApplicationShutdownService shutdownService,
        AppSettings settings,
        IImpSsoSessionStore ssoSessionStore,
        ISessionUsageAccumulator sessionUsageAccumulator,
        SessionHistoryCoordinator sessionHistory,
        SessionNavigationStore sessionNavigation,
        SettingsViewModel settingsViewModel,
        KnowledgeViewModel knowledgePageVm,
        ContextSidebarViewModel sidebar,
        FileEditorViewModel fileEditor,
        ComposerKnowledgeViewModel composerKnowledge,
        ComposerHarnessViewModel composerHarness,
        ITaskListChangedNotifier taskListChangedNotifier,
        PageViewFactory pageViewFactory,
        ChatPageViewModel chatPage,
        ScheduleViewModel schedulePageVm)
    {
        _storage = storage;
        _workspaceContext = workspaceContext;
        _mcpRegistry = mcpRegistry;
        _sessionTurns = sessionTurns;
        _composer = composer;
        _layout = layout;
        _navigation = navigation;
        _chatScroll = chatScroll;
        _uiCache = uiCache;
        _shutdownService = shutdownService;
        _sessionHistory = sessionHistory;
        _sessionNavigation = sessionNavigation;
        _sessionUsageAccumulator = sessionUsageAccumulator;
        _pageViewFactory = pageViewFactory;
        _taskListChangedNotifier = taskListChangedNotifier;
        _skillCatalog = skillCatalog;
        _appSettings = settings;
        _ssoSessionStore = settings.Sso.Enabled ? ssoSessionStore : null;
        _displayedSessionId = _session.Id;
        _activeUi = _uiCache.GetOrCreate(_displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        WireSessionUsageUi(_activeUi);
        _activeUi.SetDisplayed(true);
        _sessionTurns.TurnHost.TurnCompleted += OnTurnCompleted;
        _sessionTurns.TurnHost.TurnStateChanged += OnTurnStateChanged;
        _sessionTurns.QueuedTurnPresenter.QueueChanged += OnQueuedTurnsChanged;
        Settings = settingsViewModel;
        SchedulePageVm = schedulePageVm;
        KnowledgePageVm = knowledgePageVm;
        KnowledgePageVm.KnowledgeDataChanged += OnKnowledgeDataChanged;
        _taskListChangedNotifier.TaskListChanged += OnTaskListChanged;
        _composer.AtCompletionSourcesUpdated += OnAtCompletionSourcesUpdated;
        ComposerKnowledge = composerKnowledge;
        ComposerHarness = composerHarness;
        ChatPage = chatPage;
        ChatPage.Configure(
            () => _displayedSessionId,
            () => _session,
            () => _activeUi,
            status => Settings.SettingsStatus = status,
            NotifyCommandStatesChanged,
            SyncWorkspaceContext,
            busy => IsBusy = busy,
            () => _workspaceContext.IgnorePatterns);
        Settings.McpConfigurationChanged += async (_, _) => await RefreshMcpRuntimeAsync();
        Settings.SkillConfigurationChanged += (_, _) => OnSkillConfigurationChanged();
        Settings.SettingsSaved += async (_, _) => await OnSettingsSavedAsync();
        Settings.EmbeddingApiKeyAvailabilityChanged += (_, available) =>
            ComposerKnowledge.SetEmbeddingApiKeyAvailable(available);
        Sidebar = sidebar;
        FileEditor = fileEditor;
        FileEditor.Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOpenEditorTabs));
        FileEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FileEditorViewModel.ActiveDocument) or nameof(FileEditorViewModel.HasOpenTabs))
            {
                OnPropertyChanged(nameof(HasOpenEditorTabs));
            }
        };
        ComposerKnowledge.SetEmbeddingApiKeyAvailable(Settings.HasStoredKnowledgeEmbeddingApiKey);
        _layout.ClampInitialLayout();

        LogsPath = paths.LogsPath;
        KnowledgePageVm.SetSession(_displayedSessionId);
        _ = ComposerKnowledge.LoadForSessionAsync(_displayedSessionId);
        _ = ComposerHarness.LoadForSessionAsync(_displayedSessionId);

        InitializeSsoDisplay();

        ApplySessionWorkspace();
        _activeUi.Messages.CollectionChanged += OnMessagesCollectionChanged;
        WireModifiedFilesUi(_activeUi);
        ChatPage.PendingImageAttachments.CollectionChanged += (_, _) =>
        {
            ChatPage.OnPendingImagesChanged();
            OnPropertyChanged(nameof(HasPendingImages));
        };
        ChatPage.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ChatPageViewModel.ComposerText):
                    OnPropertyChanged(nameof(ComposerText));
                    OnPropertyChanged(nameof(IsComposerEmpty));
                    break;
                case nameof(ChatPageViewModel.IsComposerEmpty):
                    OnPropertyChanged(nameof(IsComposerEmpty));
                    break;
                case nameof(ChatPageViewModel.IsAtCompletionOpen):
                    OnPropertyChanged(nameof(IsAtCompletionOpen));
                    break;
                case nameof(ChatPageViewModel.SelectedAtCompletionIndex):
                    OnPropertyChanged(nameof(SelectedAtCompletionIndex));
                    break;
            }
        };
        AppThemeManager.ThemeChanged += OnAppThemeChanged;
        CurrentPageView = _pageViewFactory.GetOrCreate(CurrentPage);
        _ = InitializeAsync();
    }

    public bool IsLightTheme => AppThemeManager.CurrentKind == AppThemeKind.Light;

    public string ThemeToggleGlyph => IsLightTheme ? "☾" : "☀";

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
        await Settings.InitializeAsync().ConfigureAwait(true);
        await RefreshMcpRuntimeAsync().ConfigureAwait(true);

        await RefreshSessionHistoryAsync().ConfigureAwait(true);
    }

    public ObservableCollection<ChatMessageViewModel> Messages => _activeUi.Messages;

    public ObservableCollection<ModifiedFileViewModel> ModifiedFiles => _activeUi.ModifiedFiles;

    public bool HasModifiedFiles => _activeUi.HasModifiedFiles;

    public int ModifiedFilesCount => ModifiedFiles.Count;

    public string ModifiedFilesHeader => $"已修改 {ModifiedFilesCount} 个文件";

    public ObservableCollection<AgentRecordGroupViewModel> AgentRecordGroups => _sessionHistory.AgentRecordGroups;
    public ObservableCollection<QueuedTurnViewModel> QueuedTurns => _sessionTurns.QueuedTurnPresenter.GetForSession(_displayedSessionId);
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

    /// <summary>Bottom margin aligning the context sidebar split line with the composer resize line.</summary>
    public Thickness ContextSidebarComposerSplitBottomMargin => new(0, 0, 0, ComposerHeight);

    public bool IsContextSidebarVisible => _appSettings.Ui.ContextSidebarVisible;

    public GridLength ContextSidebarEdgeGutterWidth =>
        IsContextSidebarVisible ? new GridLength(12) : new GridLength(0);

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
    private bool isBusy;

    [ObservableProperty]
    private bool isLoadingSession;

    [ObservableProperty]
    private string shutdownStatusText = "正在关闭…";

    [ObservableProperty]
    private AppPage currentPage = AppPage.Chat;

    [ObservableProperty]
    private UserControl? currentPageView;

    [ObservableProperty]
    private string currentSessionTitle = "New Chat";

    [ObservableProperty]
    private string sessionUsageLine = string.Empty;

    [ObservableProperty]
    private string activeWorkspaceName = "No workspace";

    [ObservableProperty]
    private string ssoDisplayName = string.Empty;

    [ObservableProperty]
    private bool isSsoUserVisible;

    public ScheduleViewModel SchedulePageVm { get; }
    public KnowledgeViewModel KnowledgePageVm { get; }

    public ComposerKnowledgeViewModel ComposerKnowledge { get; }

    public ComposerHarnessViewModel ComposerHarness { get; }

    public ChatPageViewModel ChatPage { get; }

    public string ComposerText
    {
        get => ChatPage.ComposerText;
        set => ChatPage.ComposerText = value;
    }

    public bool IsComposerEmpty => ChatPage.IsComposerEmpty;

    public bool IsAtCompletionOpen
    {
        get => ChatPage.IsAtCompletionOpen;
        set => ChatPage.IsAtCompletionOpen = value;
    }

    public int SelectedAtCompletionIndex
    {
        get => ChatPage.SelectedAtCompletionIndex;
        set => ChatPage.SelectedAtCompletionIndex = value;
    }

    public ObservableCollection<AtCompletionItemViewModel> AtCompletionItems => ChatPage.AtCompletionItems;

    public ObservableCollection<PendingImageAttachmentViewModel> PendingImageAttachments => ChatPage.PendingImageAttachments;

    public bool HasPendingImages => ChatPage.HasPendingImages;

    public IAsyncRelayCommand SendCommand => ChatPage.SendCommand;

    public IRelayCommand StopCommand => ChatPage.StopCommand;

    public IAsyncRelayCommand SelectImagesCommand => ChatPage.SelectImagesCommand;

    public IRelayCommand RemovePendingImageCommand => ChatPage.RemovePendingImageCommand;

    public IRelayCommand RemoveQueuedTurnCommand => ChatPage.RemoveQueuedTurnCommand;

    public string SettingsStatus
    {
        get => Settings.SettingsStatus;
        set => Settings.SettingsStatus = value;
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = AppPageExtensions.Parse(page);
    }

    void INavigationService.Navigate(AppPage page) => CurrentPage = page;

    [RelayCommand]
    private void SsoLogout()
    {
        if (!_navigation.TryConfirmSsoLogout())
        {
            return;
        }

        _navigation.ClearSsoSession();
        Application.Current.Shutdown(0);
    }

    private void InitializeSsoDisplay()
    {
        var (displayName, isVisible) = _navigation.GetSsoDisplayState();
        SsoDisplayName = displayName;
        IsSsoUserVisible = isVisible;
    }

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        var next = AppThemeManager.CurrentKind == AppThemeKind.Light
            ? AppThemeKind.Dark
            : AppThemeKind.Light;
        AppThemeManager.SetTheme(next, _appSettings.Ui);
        NotifyThemeToggleStateChanged();
        await _layout.PersistNowAsync();
    }

    [RelayCommand]
    private async Task ToggleContextSidebarAsync()
    {
        SetContextSidebarVisible(!_appSettings.Ui.ContextSidebarVisible);
        await _layout.PersistNowAsync();
    }

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        NotifyThemeToggleStateChanged();
        if (_savedChatView is not null)
        {
            _ = _savedChatView.LoadMessagesAsync(_activeUi.Messages, _activeUi.ShowToolCalls);
        }
    }

    private void NotifyThemeToggleStateChanged()
    {
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(ThemeToggleGlyph));
        OnPropertyChanged(nameof(ThemeToggleToolTip));
    }

    public void SetContextSidebarVisible(bool visible) =>
        _layout.SetContextSidebarVisible(visible, NotifyContextSidebarLayoutChanged);

    public void UpdateComposerHeight(double height)
    {
        _layout.UpdateComposerHeight(height);
        OnPropertyChanged(nameof(ComposerHeight));
        OnPropertyChanged(nameof(ContextSidebarComposerSplitBottomMargin));
    }

    public void UpdateContextSidebarWidth(double width) =>
        _layout.UpdateContextSidebarWidth(width);

    public void UpdateNavigationSidebarWidth(double width) =>
        _layout.UpdateNavigationSidebarWidth(width);

    /// <summary>将 WebChatView 绑定到当前活跃 UI 控制器，用于消息的增量渲染。</summary>
    public void AttachChatView(Controls.WebChatView chatView)
    {
        _savedChatView = chatView;
        _uiCache.AttachChatViewToAll(chatView);
        _activeUi.ChatView = chatView;
        _ = chatView.LoadMessagesAsync(_activeUi.Messages, _activeUi.ShowToolCalls);
    }

    private void NotifyContextSidebarLayoutChanged()
    {
        OnPropertyChanged(nameof(IsContextSidebarVisible));
        OnPropertyChanged(nameof(ContextSidebarEdgeGutterWidth));
        OnPropertyChanged(nameof(ContextSidebarWidth));
        OnPropertyChanged(nameof(ContextSidebarToggleToolTip));
        ContextSidebarLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task PersistUiLayoutForSidebarAsync() => _layout.PersistNowAsync();

    [RelayCommand]
    private async Task NewSession()
    {
        CancelPendingSessionLoad();
        IsLoadingSession = false;
        var previousSession = _session;
        _session = AgentSession.Create("New Chat");
        SwitchDisplayedSession(_session);
        CurrentSessionTitle = _session.Title;
        ComposerText = string.Empty;
        PendingImageAttachments.Clear();
        UpdateDisplayedBusyState();
        CurrentPage = AppPage.Chat;
        KnowledgePageVm.SetSession(_displayedSessionId);
        _ = ComposerKnowledge.LoadForSessionAsync(_displayedSessionId);
        _ = ComposerHarness.LoadForSessionAsync(_displayedSessionId);
        ApplySessionWorkspace();
        _ = SaveSessionInBackgroundAsync(previousSession);
        await _storage.SaveSessionAsync(_session);
        _sessionNavigation.Invalidate(_session.Id);
        await RefreshSessionHistoryAsync();
        NotifyCommandStatesChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearContext))]
    private async Task ClearContextAsync()
    {
        var confirm = MessageBox.Show(
            "将清空当前对话在模型中的全部可见历史（用户、助手、工具与压缩记录），并清空 Coding 任务计划。\n\n会话 ID、工作区与标题会保留；磁盘上的 transcript 归档不会删除。\n\n下次发送消息时会重新构建系统提示（工作区、工具、技能等）。",
            "清空上下文",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (_sessionTurns.TurnHost.IsRunning(_displayedSessionId))
        {
            _sessionTurns.TurnHost.Cancel(_displayedSessionId);
        }

        _session = _session.WithMessages(Array.Empty<ChatMessage>());
        _activeUi.Messages.Clear();
        await _storage.ClearConversationDisplayAsync(_session.Id);
        PendingImageAttachments.Clear();
        await ComposerHarness.ClearTaskPlanAsync();
        _taskListChangedNotifier.Notify(_session.Id);

        await _storage.SaveSessionAsync(_session);
        _sessionNavigation.Invalidate(_session.Id);
        Settings.SettingsStatus = "上下文已清空。";
        NotifyCommandStatesChanged();
    }

    private bool CanClearContext() => Messages.Count > 0 && !IsBusy;

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasChatMessages));
        ClearContextCommand.NotifyCanExecuteChanged();

        if (IsBusy && e.Action == NotifyCollectionChangedAction.Add)
        {
            _chatScroll.ScrollToBottom();
        }
    }

    private void OnModifiedFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasModifiedFiles));
        OnPropertyChanged(nameof(ModifiedFilesCount));
        OnPropertyChanged(nameof(ModifiedFilesHeader));
    }

    private void WireModifiedFilesUi(SessionTurnUiController ui)
    {
        ui.ModifiedFiles.CollectionChanged += OnModifiedFilesCollectionChanged;
        OnPropertyChanged(nameof(ModifiedFiles));
        OnPropertyChanged(nameof(HasModifiedFiles));
        OnPropertyChanged(nameof(ModifiedFilesCount));
        OnPropertyChanged(nameof(ModifiedFilesHeader));
    }

    private void UnwireModifiedFilesUi(SessionTurnUiController ui) =>
        ui.ModifiedFiles.CollectionChanged -= OnModifiedFilesCollectionChanged;

    private void NotifyCommandStatesChanged()
    {
        SendCommand.NotifyCanExecuteChanged();
        ClearContextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasChatMessages));
    }

    private void OnQueuedTurnsChanged(string sessionId)
    {
        if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(QueuedTurns));
            OnPropertyChanged(nameof(HasQueuedTurns));
        }
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
        CurrentPage = AppPage.Chat;
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

        if (_sessionTurns.TurnHost.IsRunning(item.Id))
        {
            _sessionTurns.TurnHost.Cancel(item.Id);
        }

        _sessionTurns.TurnHost.ClearQueue(item.Id);
        _sessionTurns.QueuedTurnPresenter.RemoveSession(item.Id);

        _uiCache.Remove(item.Id);
        await _storage.DeleteSessionAsync(item.Id);
        _sessionNavigation.Invalidate(item.Id);

        if (string.Equals(_session.Id, item.Id, StringComparison.Ordinal))
        {
            _session = AgentSession.Create("New Chat");
            SwitchDisplayedSession(_session);
            CurrentSessionTitle = _session.Title;
            ComposerText = string.Empty;
            PendingImageAttachments.Clear();
            ApplySessionWorkspace();
            CurrentPage = AppPage.Chat;
        }

        await RefreshSessionHistoryAsync();
        Settings.SettingsStatus = "对话已删除。";
        NotifyCommandStatesChanged();
    }

    public void AddPendingImages(IEnumerable<ImageAttachment> images) =>
        ChatPage.AddPendingImages(images);

    private void RequestScrollToBottom() => _chatScroll.ScrollToBottom();

    private void RequestScrollToBottomImmediate() => _chatScroll.ScrollToBottomImmediate();

    private void WireSessionUsageUi(SessionTurnUiController ui)
    {
        ui.OnUsageRecorded = snapshot => SessionUsageLine = SessionUsageFormatter.Format(snapshot);
        SessionUsageLine = SessionUsageFormatter.Format(_sessionUsageAccumulator.Get(_displayedSessionId));
    }

    private void SwitchDisplayedSession(AgentSession session, bool renderExistingMessages = true)
    {
        _activeUi.SetDisplayed(false);
        _activeUi.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        UnwireModifiedFilesUi(_activeUi);
        _displayedSessionId = session.Id;
        _session = session;
        _activeUi = _uiCache.GetOrCreate(_displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        WireSessionUsageUi(_activeUi);
        WireModifiedFilesUi(_activeUi);
        _activeUi.SetDisplayed(true);
        // 切换会话时重新绑定 WebChatView（如果已经初始化）
        if (_activeUi.ChatView is null)
        {
            _activeUi.ChatView = _savedChatView;
        }

        if (renderExistingMessages && _savedChatView is not null)
        {
            _ = _savedChatView.LoadMessagesAsync(_activeUi.Messages, _activeUi.ShowToolCalls);
        }

        _activeUi.Messages.CollectionChanged += OnMessagesCollectionChanged;
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(QueuedTurns));
        OnPropertyChanged(nameof(HasQueuedTurns));
        UpdateDisplayedBusyState();
        KnowledgePageVm.SetSession(_displayedSessionId);
        _ = ComposerKnowledge.LoadForSessionAsync(_displayedSessionId);
        _ = ComposerHarness.LoadForSessionAsync(_displayedSessionId);
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

    private void OnKnowledgeDataChanged()
    {
        _ = ComposerKnowledge.LoadForSessionAsync(_displayedSessionId);
        _ = ComposerHarness.LoadForSessionAsync(_displayedSessionId);
    }

    private void OnTaskListChanged(string sessionId)
    {
        if (!string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
        {
            return;
        }

        Application.Current?.Dispatcher.InvokeAsync(() => _ = ComposerHarness.RefreshTasksAsync());
    }

    private void OnTurnCompleted(object? sender, SessionTurnCompletedEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (string.Equals(e.SessionId, _displayedSessionId, StringComparison.Ordinal))
            {
                _session = e.Session;
                CurrentSessionTitle = _session.Title;
                UpdateDisplayedBusyState();
                _ = ComposerHarness.RefreshTasksAsync();
            }

            _sessionNavigation.Invalidate(e.SessionId);
            RequestRefreshSessionHistory();
            if (_sessionTurns.QueuedTurnPresenter.TryProcessNext(e, out var queueError))
            {
                if (string.Equals(e.SessionId, _displayedSessionId, StringComparison.Ordinal))
                {
                    UpdateDisplayedBusyState();
                    if (queueError is not null)
                    {
                        Settings.SettingsStatus = queueError;
                    }
                }
            }

            NotifyCommandStatesChanged();
        });
    }

    private void UpdateDisplayedBusyState() => ChatPage.UpdateDisplayedBusyState();

    private void StopSession(string sessionId)
    {
        _sessionTurns.TurnHost.Cancel(sessionId);
        _sessionTurns.QueuedTurnPresenter.Clear(sessionId);
    }

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
        Settings.SettingsStatus = $"当前对话工作区：{folderName}";
    }

    private async Task OnSettingsSavedAsync()
    {
        _uiCache.ApplyShowToolCalls(Settings.Settings.Ui.ShowToolCalls);
        await _activeUi.HydrateFromSessionAsync(_session).ConfigureAwait(true);
        await RefreshMcpRuntimeAsync().ConfigureAwait(true);
        ApplySessionWorkspace();
        CurrentPage = AppPage.Chat;
    }

    public Task OpenWorkspaceFileInEditorAsync(string path) =>
        FileEditor.OpenFileAsync(path, _session.ActiveWorkspace);

    public Task OpenWorkspaceFileForPreviewAsync(string relativeOrFullPath)
    {
        var fullPath = ResolveWorkspaceFilePath(relativeOrFullPath);
        if (fullPath is null)
        {
            MessageBox.Show(
                "无法解析文件路径。请先配置工作区。",
                "无法预览",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        return FileEditor.OpenFileAsync(fullPath, _session.ActiveWorkspace, readOnly: true);
    }

    [RelayCommand]
    private Task OpenModifiedFileAsync(ModifiedFileViewModel? file)
    {
        if (file is null)
        {
            return Task.CompletedTask;
        }

        return OpenWorkspaceFileForPreviewAsync(file.RelativePath);
    }

    private string? ResolveWorkspaceFilePath(string relativeOrFullPath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrFullPath))
        {
            return null;
        }

        if (Path.IsPathRooted(relativeOrFullPath))
        {
            return Path.GetFullPath(relativeOrFullPath);
        }

        if (string.IsNullOrWhiteSpace(_session.ActiveWorkspace))
        {
            return null;
        }

        var normalized = relativeOrFullPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(_session.ActiveWorkspace, normalized));
    }

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

    [RelayCommand(CanExecute = nameof(CanOpenWorkspaceTreeNodeInExplorer))]
    private void OpenWorkspaceTreeNodeInExplorer(WorkspaceTreeNodeViewModel? node)
    {
        if (!CanOpenWorkspaceTreeNodeInExplorer(node) || node is null || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(node.FullPath);
            // 对文件取其所在目录，对文件夹直接用其自身
            var targetPath = node.IsDirectory ? fullPath : Path.GetDirectoryName(fullPath);
            if (targetPath is null)
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法打开文件夹：{exception.Message}",
                "打开失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private bool CanOpenWorkspaceTreeNodeInExplorer(WorkspaceTreeNodeViewModel? node) =>
        node is not null
        && !node.IsPlaceholder
        && !node.IsExpanderPlaceholder
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
    private async Task CloseEditorTab(EditorDocumentViewModel? document) =>
        await FileEditor.CloseTabAsync(document).ConfigureAwait(true);

    public Task<bool> ConfirmCloseEditorTabsAsync() => FileEditor.TryCloseAllTabsAsync();

    public void UpdateEditorPaneWidth(double width) =>
        _layout.UpdateEditorPaneWidth(width);

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
                Settings.SettingsStatus = "目标不存在或已被删除。";
                Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
                return;
            }

            RefreshAtCompletionSources();
            Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
            Settings.SettingsStatus = $"已删除{kind}「{node.Name}」。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法删除「{node.Name}」：{exception.Message}",
                "删除失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Settings.SettingsStatus = $"删除失败：{exception.Message}";
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

    public void UpdateComposerCompletion(string composerText, int caretIndex)
    {
        _composerCaretIndex = caretIndex;
        ChatPage.UpdateComposerCompletion(composerText, caretIndex);
    }

    private void OnAtCompletionSourcesUpdated()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!ChatPage.IsAtCompletionOpen)
            {
                return;
            }

            ChatPage.UpdateAtCompletion(ComposerText, _composerCaretIndex);
        });
    }

    public void UpdateAtCompletion(string composerText, int caretIndex) =>
        ChatPage.UpdateAtCompletion(composerText, caretIndex);

    public void MoveAtCompletionSelection(int delta) =>
        ChatPage.MoveAtCompletionSelection(delta);

    public bool TryAcceptAtCompletion(int caretIndex, out int newCaretIndex) =>
        ChatPage.TryAcceptAtCompletion(caretIndex, out newCaretIndex);

    public void CloseAtCompletion() => ChatPage.CloseAtCompletion();

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
        var toSave = await _sessionNavigation.SaveIfNotEmptyAsync(session);
        if (toSave is null)
        {
            return;
        }

        if (string.Equals(toSave.Id, _displayedSessionId, StringComparison.Ordinal))
        {
            _session = toSave;
        }
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
                Settings.SettingsStatus = $"保存对话失败：{ex.Message}");
        }
    }

    private void RequestRefreshSessionHistory() =>
        _sessionHistory.RequestRefresh(RefreshSessionHistoryAsync);

    private async Task RefreshSessionHistoryAsync()
    {
        await _sessionHistory.RefreshAsync(_session.Id, _sessionTurns.TurnHost.IsRunning, StopSession);
        OnPropertyChanged(nameof(HasAgentRecords));
    }

    public bool HasAgentRecords => _sessionHistory.HasAgentRecords;

    public async Task OpenSessionByIdAsync(string sessionId)
    {
        CurrentPage = AppPage.Chat;
        await LoadSessionInternalAsync(sessionId);
    }

    private void CancelPendingSessionLoad() =>
        Interlocked.Increment(ref _sessionLoadGeneration);

    private bool IsSessionLoadCurrent(int loadGeneration) =>
        loadGeneration == Volatile.Read(ref _sessionLoadGeneration);

    private async Task LoadSessionInternalAsync(string sessionId)
    {
        var loadGeneration = Interlocked.Increment(ref _sessionLoadGeneration);
        IsLoadingSession = true;
        Settings.SettingsStatus = "正在加载对话…";
        try
        {
            var snapshot = await _sessionNavigation.LoadSnapshotAsync(sessionId).ConfigureAwait(true);
            if (!IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            if (snapshot is null)
            {
                Settings.SettingsStatus = "无法加载该对话。";
                return;
            }

            SwitchDisplayedSession(snapshot.Session, renderExistingMessages: false);
            CurrentSessionTitle = _session.Title;
            KnowledgePageVm.SetSession(_displayedSessionId);
            ComposerText = string.Empty;
            PendingImageAttachments.Clear();

            if (!IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            if (snapshot.DisplayMessages.Count > 0)
            {
                await _activeUi.HydrateDisplayAsync(_session, snapshot.DisplayMessages).ConfigureAwait(true);
            }
            else
            {
                await _activeUi.HydrateFromSessionAsync(_session).ConfigureAwait(true);
            }

            if (!IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            ApplySessionWorkspace();
            UpdateDisplayedBusyState();
            Settings.SettingsStatus = $"已加载对话：{_session.Title}";

        }
        finally
        {
            if (IsSessionLoadCurrent(loadGeneration))
            {
                IsLoadingSession = false;
                NotifyCommandStatesChanged();
            }
        }
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

    public bool HasPendingShutdownWork => _sessionTurns.HasActiveWork;

    public async Task ShutdownAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        _workspaceBridge.Dispose();

        await _shutdownService.ShutdownAsync(progress, cancellationToken: cancellationToken).ConfigureAwait(false);
        _shutdownCompleted = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnsubscribeEvents();
    }

    private void UnsubscribeEvents()
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        _sessionTurns.TurnHost.TurnCompleted -= OnTurnCompleted;
        _sessionTurns.TurnHost.TurnStateChanged -= OnTurnStateChanged;
        KnowledgePageVm.KnowledgeDataChanged -= OnKnowledgeDataChanged;
        _taskListChangedNotifier.TaskListChanged -= OnTaskListChanged;
        _composer.AtCompletionSourcesUpdated -= OnAtCompletionSourcesUpdated;
        _activeUi.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        UnwireModifiedFilesUi(_activeUi);
        _copyNoticeCts?.Cancel();
        _copyNoticeCts?.Dispose();
        _layout.Dispose();
        _sessionHistory.Dispose();
        _workspaceBridge.Dispose();
    }

    partial void OnCurrentPageChanged(AppPage value)
    {
        CurrentPageView = _pageViewFactory.GetOrCreate(value);
        _navigation.HandlePageChanged(value.ToPageKey(), Settings, SchedulePageVm, KnowledgePageVm);
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

    private void RefreshAtCompletionSources(bool reloadSkills = false) =>
        _composer.RefreshSources(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns, reloadSkills);

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
