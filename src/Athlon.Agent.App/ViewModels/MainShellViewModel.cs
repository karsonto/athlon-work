using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.ComputerUse;
using Athlon.Agent.App.Services.Diagnostics;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

using System.Windows.Controls;
using System.Windows.Media;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Navigation;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.Windows;
using Athlon.Agent.Infrastructure.Ssh;
using MaterialDesignThemes.Wpf;

namespace Athlon.Agent.App.ViewModels;

public partial class MainShellViewModel : ObservableObject, IDisposable, ISessionHost, INavigationService
{
    private readonly IFileStorageService _storage;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly IMcpRegistry _mcpRegistry;
    private readonly AppSettings _appSettings;
    private readonly IImpSsoSessionStore? _ssoSessionStore;
    private readonly IAgentSkillCatalog _skillCatalog;
    /// <summary>For switch-diagnostic queueing gaps, which are logged rather than emitted.</summary>
    private readonly IAppLogger _logger;
    private readonly SessionTurnCoordinator _sessionTurns;
    private readonly SessionCompactionService _compactionService;
    private readonly ComposerCoordinator _composer;
    private readonly LayoutCoordinator _layout;
    private readonly NavigationCoordinator _navigation;
    private readonly IChatScrollService _chatScroll;
    private readonly SessionUiCache _uiCache;
    private readonly SessionRuntimeStore _runtime;
    private readonly ApplicationShutdownService _shutdownService;
    private readonly SessionHistoryCoordinator _sessionHistory;
    private readonly SessionNavigationStore _sessionNavigation;
    private readonly WorkspaceSessionBridge _workspaceBridge = new();
    private readonly ISessionUsageAccumulator _sessionUsageAccumulator;
    private readonly PageViewFactory _pageViewFactory;
    private readonly ITaskListChangedNotifier _taskListChangedNotifier;
    private readonly ISessionTaskListStore _taskListStore;
    private readonly IPlanRunStore _planRunStore;
    private readonly IPlanArtifactStore _planArtifactStore;
    private readonly ISessionPlanArtifactsClearer _planArtifactsClearer;
    private readonly IPlanContinuationTracker _planContinuationTracker;
    private readonly ILocalizationService _loc;
    private readonly IUserNotifier _notifier;
    private readonly SshWorkspaceConnectionService _sshConnection;
    private readonly ICredentialStore _credentialStore;
    private readonly ISshWorkspaceClient _sshClient;
    /// <summary>
    /// While a session is being deleted its directory must not be re-read: a pending task-list
    /// refresh would reopen tasks.json (or re-create the directory) and race the delete.
    /// </summary>
    private bool _suppressTaskListRefresh;
    private readonly SshWorkspaceTransferService _sshTransfer;
    private readonly ILongTermMemory _longTermMemory;
    private readonly AthlonWebStaticServer _athlonWebServer;

    private AgentSession _session = AgentSession.Create("New Chat");
    private string _displayedSessionId;
    private SessionTurnUiController _activeUi;
    /// <summary>
    /// Completion of the displayed session's full payload. Turn-start paths await this so a
    /// metadata-only shell is never used as the model context. Null when nothing is pending.
    /// </summary>
    private Task? _displayedSessionReady;
    private bool _shutdownCompleted;
    private bool _disposed;
    private int _sessionLoadGeneration;
    private int _composerCaretIndex;
    private Controls.WebChatView? _savedChatView;
    private ConversationDisplayCursor? _olderDisplayCursor;
    private bool _olderHistoryLoadInProgress;
    private EventHandler? _onMcpConfigurationChanged;
    private EventHandler? _onSkillConfigurationChanged;
    private EventHandler? _onSettingsSaved;

    public MainShellViewModel(
        IFileStorageService storage,
        IActiveWorkspaceContext workspaceContext,
        IMcpRegistry mcpRegistry,
        IAppPathProvider paths,
        IAgentSkillCatalog skillCatalog,
        SessionTurnCoordinator sessionTurns,
        SessionCompactionService compactionService,
        ComposerCoordinator composer,
        LayoutCoordinator layout,
        NavigationCoordinator navigation,
        IChatScrollService chatScroll,
        SessionUiCache uiCache,
        SessionRuntimeStore runtimeStore,
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
        WorkspacePaneViewModel workspacePane,
        ComposerKnowledgeViewModel composerKnowledge,
        ComposerHarnessViewModel composerHarness,
        DebugActionBarViewModel debugBar,
        PlanActionBarViewModel planBar,
        QuestionBarViewModel questionBar,
        ITaskListChangedNotifier taskListChangedNotifier,
        ISessionTaskListStore taskListStore,
        IPlanRunStore planRunStore,
        IPlanArtifactStore planArtifactStore,
        ISessionPlanArtifactsClearer planArtifactsClearer,
        IPlanContinuationTracker planContinuationTracker,
        PageViewFactory pageViewFactory,
        ChatPageViewModel chatPage,
        ScheduleViewModel schedulePageVm,
        SkillHubViewModel skillHubVm,
        ILocalizationService localization,
        IUserNotifier notifier,
        SshWorkspaceConnectionService sshConnection,
        ICredentialStore credentialStore,
        ISshWorkspaceClient sshClient,
        ILongTermMemory longTermMemory,
        AppUpdateService updateService,
        AthlonWebStaticServer athlonWebServer,
        IAppLogger logger)
    {
        _storage = storage;
        _workspaceContext = workspaceContext;
        _mcpRegistry = mcpRegistry;
        _sessionTurns = sessionTurns;
        _compactionService = compactionService;
        _composer = composer;
        _layout = layout;
        _navigation = navigation;
        _chatScroll = chatScroll;
        _uiCache = uiCache;
        _runtime = runtimeStore;
        _shutdownService = shutdownService;
        _sessionHistory = sessionHistory;
        _sessionNavigation = sessionNavigation;
        _sessionUsageAccumulator = sessionUsageAccumulator;
        _pageViewFactory = pageViewFactory;
        _taskListChangedNotifier = taskListChangedNotifier;
        _taskListStore = taskListStore;
        _planRunStore = planRunStore;
        _planArtifactStore = planArtifactStore;
        _planArtifactsClearer = planArtifactsClearer;
        _planContinuationTracker = planContinuationTracker;
        _loc = localization;
        _notifier = notifier;
        _sshConnection = sshConnection;
        _credentialStore = credentialStore;
        _sshClient = sshClient;
        _sshTransfer = new SshWorkspaceTransferService(sshClient, notifier);
        _longTermMemory = longTermMemory;
        _athlonWebServer = athlonWebServer;
        _skillCatalog = skillCatalog;
        _logger = logger;
        _appSettings = settings;
        _contextSidebarEdgeGutterWidth = 0;
        _ssoSessionStore = settings.Sso.Enabled ? ssoSessionStore : null;
        _displayedSessionId = _session.Id;
        _sshConnection.SetDefaultSession(_displayedSessionId);
        _runtime.Attach(_session, hydrated: true);
        _activeUi = _uiCache.GetOrCreate(_displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        WireSessionUsageUi(_activeUi);
        _activeUi.SetDisplayed(true);
        _sessionTurns.TurnHost.TurnCompleted += OnTurnCompleted;
        _sessionTurns.TurnHost.TurnStateChanged += OnTurnStateChanged;
        _sessionTurns.QueuedTurnPresenter.QueueChanged += OnQueuedTurnsChanged;
        Settings = settingsViewModel;
        SchedulePageVm = schedulePageVm;
        SkillHubVm = skillHubVm;
        SkillHubVm.Configure(
            onSkillsInstalled: OnSkillConfigurationChanged,
            navigateToSettings: () => CurrentPage = AppPage.Settings);
        KnowledgePageVm = knowledgePageVm;
        KnowledgePageVm.KnowledgeDataChanged += OnKnowledgeDataChanged;
        _taskListChangedNotifier.TaskListChanged += OnTaskListChanged;
        _composer.AtCompletionSourcesUpdated += OnAtCompletionSourcesUpdated;
        ComposerKnowledge = composerKnowledge;
        ComposerHarness = composerHarness;
        DebugBar = debugBar;
        PlanBar = planBar;
        QuestionBar = questionBar;
        DebugBar.Configure(
            () => _displayedSessionId,
            () => _session,
            () => _activeUi,
            ShowShellToast);
        PlanBar.Configure(
            () => _displayedSessionId,
            () => _session,
            session => _session = session,
            ShowShellToast,
            StartFromApprovedPlanAsync,
            setComposerHint: SetComposerStatus,
            onPlanTimeline: _ => RefreshPlanCard(),
            onPlanTimelineCleared: () => _activeUi.ClearPlanReady(),
            setComposerFocus: () => chatPage.RequestFocusComposer());
        QuestionBar.Configure(
            () => _displayedSessionId,
            ShowShellToast,
            OnUserQuestionAnswered,
            sessionId => _sessionTurns.TurnHost.IsRunning(sessionId));
        ComposerHarness.OnModePickerOpened = () => IsPlusMenuOpen = false;
        ComposerHarness.OnModeChangedAsync = OnComposerModeChangedAsync;
        ComposerHarness.OnPlanAutoCleared = _ =>
            ShowShellToast(_loc["Harness_TaskPlanCleared"], ShellToastKind.Success);
        ComposerHarness.OnAutoContinueSettingChangedAsync = OnAutoContinueSettingChangedAsync;
        ChatPage = chatPage;
        ChatPage.Configure(
            () => _displayedSessionId,
            () => _session,
            () => _activeUi,
            ShowShellToast,
            SetComposerStatus,
            NotifyCommandStatesChanged,
            SyncWorkspaceContext,
            busy => IsBusy = busy,
            () => _workspaceContext.IgnorePatterns,
            TryCancelCompaction,
            CreateSlashCommandContext,
            EnsureDisplayedSessionReadyAsync);
        _onMcpConfigurationChanged = (_, _) => _ = RunGuardedAsync(RefreshMcpRuntimeAsync, "MCP runtime refresh");
        _onSkillConfigurationChanged = (_, _) => OnSkillConfigurationChanged();
        _onSettingsSaved = (_, _) => _ = RunGuardedAsync(OnSettingsSavedAsync, "settings saved handler");
        Settings.McpConfigurationChanged += _onMcpConfigurationChanged;
        Settings.SkillConfigurationChanged += _onSkillConfigurationChanged;
        Settings.SettingsSaved += _onSettingsSaved;
        Sidebar = sidebar;
        Sidebar.SetActivateHandlers(ToggleSkillFromSidebarAsync, ActivateMcpFromSidebarAsync);
        Sidebar.Refresh(_appSettings);
        FileEditor = fileEditor;
        WorkspacePane = workspacePane;
        WorkspacePane.PropertyChanged += OnWorkspacePanePropertyChanged;
        FileEditor.Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOpenEditorTabs));
        FileEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FileEditorViewModel.ActiveDocument) or nameof(FileEditorViewModel.HasOpenTabs))
            {
                OnPropertyChanged(nameof(HasOpenEditorTabs));
            }
        };
        _layout.ClampInitialLayout();

        UpdateBanner = new AppUpdateBannerViewModel(updateService, localization, ShowShellToast);

        LogsPath = paths.LogsPath;
        KnowledgePageVm.SetSession(_displayedSessionId);
        _ = ComposerKnowledge.LoadForSessionAsync(_displayedSessionId);
        _ = ComposerHarness.LoadForSessionAsync(_displayedSessionId);

        InitializeSsoDisplay();

        ApplySessionWorkspace();
        ContextOccupancy.CompactCommand = CompactContextCommand;
        ContextOccupancy.ClearCommand = ClearContextCommand;
        ContextOccupancy.IsCompacting = IsCompacting;
        _activeUi.Messages.CollectionChanged += OnMessagesCollectionChanged;
        ChatPage.PendingImageAttachments.CollectionChanged += (_, _) =>
        {
            ChatPage.OnPendingImagesChanged();
            OnPropertyChanged(nameof(HasPendingImages));
            OnPropertyChanged(nameof(HasPendingAttachments));
        };
        ChatPage.PendingDocumentAttachments.CollectionChanged += (_, _) =>
        {
            ChatPage.OnPendingDocumentsChanged();
            OnPropertyChanged(nameof(HasPendingDocuments));
            OnPropertyChanged(nameof(HasPendingAttachments));
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
                case nameof(ChatPageViewModel.IsReadingAttachments):
                    OnPropertyChanged(nameof(IsReadingAttachments));
                    break;
                case nameof(ChatPageViewModel.HasPendingAttachments):
                    OnPropertyChanged(nameof(HasPendingAttachments));
                    break;
                case nameof(ChatPageViewModel.HasPendingDocuments):
                    OnPropertyChanged(nameof(HasPendingDocuments));
                    break;
                case nameof(ChatPageViewModel.IsSpeechInputAvailable):
                    OnPropertyChanged(nameof(IsSpeechInputAvailable));
                    OnPropertyChanged(nameof(SendButtonToolTip));
                    break;
                case nameof(ChatPageViewModel.IsSpeechListening):
                    OnPropertyChanged(nameof(IsSpeechListening));
                    OnPropertyChanged(nameof(SendButtonToolTip));
                    break;
                case nameof(ChatPageViewModel.SendButtonToolTip):
                    OnPropertyChanged(nameof(SendButtonToolTip));
                    break;
            }
        };
        AppThemeManager.ThemeChanged += OnAppThemeChanged;
        AppCultureManager.CultureChanged += OnCultureChanged;
        RefreshLocalizedStrings();
        CurrentPageView = _pageViewFactory.GetOrCreate(CurrentPage);
        _ = InitializeAsync();
    }

    public bool IsLightTheme => AppThemeManager.CurrentKind == AppThemeKind.Light;

    public string ThemeToggleToolTip =>
        IsLightTheme ? _loc["Shell_SwitchToDark"] : _loc["Shell_SwitchToLight"];

    public bool HasChatMessages => Messages.Count > 0;

    public bool HasComputerUseTranscript =>
        Messages.Any(static message => message.IsComputerUseTranscriptVisible);

    public string ChatWelcomeTitle =>
        string.IsNullOrWhiteSpace(SsoDisplayName)
            ? _loc["Chat_WelcomeTitle"]
            : _loc.Format("Chat_WelcomeTitleWithName", SsoDisplayName.Trim());

    public string ChatWelcomeDescription => _loc["Chat_WelcomeDescription"];

    public string SsoAvatarInitial
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SsoDisplayName))
            {
                return "A";
            }

            var trimmed = SsoDisplayName.Trim();
            return trimmed[..1].ToUpperInvariant();
        }
    }

    public string SidebarAccountTitle =>
        IsSsoUserVisible && !string.IsNullOrWhiteSpace(SsoDisplayName)
            ? SsoDisplayName.Trim()
            : _loc["Nav_Account"];

    public string SidebarAccountSubtitle =>
        IsSsoUserVisible ? _loc["Sso_SignedIn"] : _loc["Nav_AccountGuest"];

    private async Task RefreshMcpRuntimeAsync()
    {
        void PublishMcpStatuses()
        {
            void Apply()
            {
                Settings.RefreshRuntimeStates();
                Sidebar.Refresh(Settings.Settings);
                RefreshAtCompletionSources();
                OnPropertyChanged(nameof(Sidebar));
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                Apply();
                return;
            }

            _ = dispatcher.InvokeAsync(Apply);
        }

        await _mcpRegistry.RefreshAsync(
            Settings.Settings.McpServers,
            cancellationToken: default,
            onStatusesChanged: PublishMcpStatuses).ConfigureAwait(true);
        PublishMcpStatuses();
    }

    private async Task ToggleSkillFromSidebarAsync(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return;
        }

        var existing = _appSettings.Skills.FirstOrDefault(skill =>
            string.Equals(skill.Name, skillName, StringComparison.OrdinalIgnoreCase));
        var currentlyEnabled = existing?.Enabled ?? true;
        var enabled = !currentlyEnabled;

        if (existing is null)
        {
            _appSettings.Skills.Add(new SkillSettings
            {
                Name = skillName,
                Enabled = enabled,
                Path = skillName
            });
        }
        else
        {
            existing.Enabled = enabled;
        }

        try
        {
            Athlon.Agent.Infrastructure.BehaviorReport.BehaviorEventManager.Instance.Record(
                Athlon.Agent.Core.BehaviorReport.BehaviorEventIds.SkillToggle,
                Athlon.Agent.Core.BehaviorReport.BehaviorEventTypes.Event,
                Athlon.Agent.Core.BehaviorReport.BehaviorEventIds.SkillToggle,
                new Dictionary<string, object?>
                {
                    ["skill_id"] = skillName,
                    ["enabled"] = enabled
                });
        }
        catch
        {
            // ignore
        }

        await _storage.SaveSettingsAsync(_appSettings).ConfigureAwait(true);
        Settings.SyncSkillsFromCatalog();
        OnSkillConfigurationChanged();
    }

    private async Task ActivateMcpFromSidebarAsync(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName) || !Sidebar.HasConfiguredMcpServers)
        {
            return;
        }

        var server = _appSettings.McpServers.FirstOrDefault(item =>
            string.Equals(item.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            return;
        }

        var action = McpSidebarActivate.Resolve(server.Enabled);
        server.Enabled = action == McpSidebarActivateAction.Enable;
        try
        {
            Athlon.Agent.Infrastructure.BehaviorReport.BehaviorEventManager.Instance.Record(
                Athlon.Agent.Core.BehaviorReport.BehaviorEventIds.McpServer,
                Athlon.Agent.Core.BehaviorReport.BehaviorEventTypes.Event,
                Athlon.Agent.Core.BehaviorReport.BehaviorEventIds.McpServer,
                new Dictionary<string, object?>
                {
                    ["server_name"] = serverName,
                    ["action"] = server.Enabled ? "enabled" : "disabled"
                });
        }
        catch
        {
            // ignore
        }

        await _storage.SaveSettingsAsync(_appSettings).ConfigureAwait(true);

        // Update sidebar immediately so other MCP tags stay clickable while connections run.
        Settings.RefreshRuntimeStates();
        Sidebar.Refresh(_appSettings);
        RefreshAtCompletionSources();
        OnPropertyChanged(nameof(Sidebar));

        try
        {
            await RefreshMcpRuntimeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer MCP toggle; sidebar already reflects the latest settings.
        }
    }

    public async Task InitializeAsync()
    {
        await Settings.InitializeAsync().ConfigureAwait(true);
        await RefreshMcpRuntimeAsync().ConfigureAwait(true);

        await RefreshSessionHistoryAsync().ConfigureAwait(true);
    }

    public ObservableCollection<ChatMessageViewModel> Messages => _activeUi.Messages;

    public ObservableCollection<AgentRecordGroupViewModel> AgentRecordGroups => _sessionHistory.AgentRecordGroups;
    public ObservableCollection<QueuedTurnViewModel> QueuedTurns => _sessionTurns.QueuedTurnPresenter.GetForSession(_displayedSessionId);
    public bool HasQueuedTurns => QueuedTurns.Count > 0;
    public ContextSidebarViewModel Sidebar { get; }
    public SettingsViewModel Settings { get; }
    public FileEditorViewModel FileEditor { get; }
    public WorkspacePaneViewModel WorkspacePane { get; }
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

    public event EventHandler<ContextSidebarLayoutChangedEventArgs>? ContextSidebarLayoutChanged;
    public event EventHandler<ContextSidebarLayoutChangedEventArgs>? NavigationSidebarLayoutChanged;

    public double NavigationSidebarWidth =>
        Math.Clamp(_appSettings.Ui.NavigationSidebarWidth, NavigationSidebarMinWidth, NavigationSidebarMaxWidth);

    public double ComposerHeight =>
        Math.Clamp(_appSettings.Ui.ComposerHeight, ComposerMinHeight, ComposerMaxHeight);

    private double _contextSidebarEdgeGutterWidth;
    private bool _contextSidebarLayoutAnimate;
    private bool _navigationSidebarLayoutAnimate;
    private double _preMaximizeContextWidth = UiLayoutConstraints.ContextSidebarDefaultWidth;

    [ObservableProperty]
    private bool isWorkspaceMaximized;

    [ObservableProperty]
    private bool isComputerUseOverlayActive;

    [ObservableProperty]
    private string computerUseActiveToolText = string.Empty;

    [ObservableProperty]
    private string computerUseAssistantSummary = string.Empty;

    private readonly HashSet<ChatMessageViewModel> _computerUseStatusMessageSubscriptions = new();

    public bool ComputerUseStatusVisible => IsComputerUseOverlayActive && IsBusy;

    public bool IsContextSidebarVisible => _appSettings.Ui.ContextSidebarVisible;

    public bool IsNavigationSidebarVisible => _appSettings.Ui.NavigationSidebarVisible;

    public GridLength ContextSidebarEdgeGutterWidth =>
        new GridLength(_contextSidebarEdgeGutterWidth);

    public double ContextSidebarWidth =>
        Math.Clamp(_appSettings.Ui.ContextSidebarWidth, ContextSidebarMinWidth, ContextSidebarMaxWidth);

    public string ContextSidebarToggleToolTip =>
        IsContextSidebarVisible ? _loc["Shell_ContextSidebarClose"] : _loc["Shell_ContextSidebarOpen"];

    public string WorkspaceMaximizeToolTip =>
        IsWorkspaceMaximized ? _loc["Workspace_Restore"] : _loc["Workspace_Maximize"];

    public string NavigationSidebarToggleToolTip =>
        IsNavigationSidebarVisible ? _loc["Shell_NavigationSidebarClose"] : _loc["Shell_NavigationSidebarOpen"];

    public bool IsToolsNavExpanded => _appSettings.Ui.ToolsNavExpanded;

    public string ToolsNavExpandGlyph => IsToolsNavExpanded ? "▾" : "▸";

    public const double ContextSidebarCollapseDragThreshold = UiLayoutConstraints.ContextSidebarCollapseDragThreshold;

    public ShellStatusFeedback StatusFeedback { get; } = new();

    public AppUpdateBannerViewModel UpdateBanner { get; }

    private CancellationTokenSource? _compactionCts;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isCompacting;

    public bool IsComposerStopVisible => IsBusy || IsCompacting;

    public bool IsComposerSendVisible => !IsCompacting;

    [ObservableProperty]
    private bool isLoadingSession;

    [ObservableProperty]
    private string shutdownStatusText = string.Empty;

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

    [ObservableProperty]
    private bool isPlusMenuOpen;

    [ObservableProperty]
    private bool isComposerMultiLine;

    public string WorkspacePanelActionLabel =>
        HasSessionWorkspace ? _loc["Context_RemoveWorkspace"] : _loc["Common_Configure"];

    public string RunOnDisplayName
    {
        get
        {
                if (!HasSessionWorkspace)
                {
                    return _loc["Shell_ChooseWorkspace"];
                }

            if (_workspaceContext.Kind == WorkspaceKind.Ssh)
            {
                var match = WorkspaceSessionResolver.FindMatch(_session, _appSettings);
                if (match is not null)
                {
                    return FormatRemoteLabel(match);
                }

                return _workspaceContext.DisplayName ?? _loc["Shell_RunOnRemote"];
            }

            return _workspaceContext.DisplayName ?? _loc["Shell_RunOnThisPc"];
        }
    }

    public bool HasSessionWorkspace =>
        !string.IsNullOrWhiteSpace(_session.ActiveWorkspace);

    public ScheduleViewModel SchedulePageVm { get; }
    public KnowledgeViewModel KnowledgePageVm { get; }
    public SkillHubViewModel SkillHubVm { get; }

    public ComposerKnowledgeViewModel ComposerKnowledge { get; }

    public ComposerHarnessViewModel ComposerHarness { get; }

    public DebugActionBarViewModel DebugBar { get; }

    public PlanActionBarViewModel PlanBar { get; }

    public QuestionBarViewModel QuestionBar { get; }

    public ContextOccupancyViewModel ContextOccupancy { get; } = new();

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

    public ObservableCollection<PendingDocumentAttachmentViewModel> PendingDocumentAttachments =>
        ChatPage.PendingDocumentAttachments;

    public bool HasPendingImages => ChatPage.HasPendingImages;

    public bool HasPendingDocuments => ChatPage.HasPendingDocuments;

    public bool HasPendingAttachments => ChatPage.HasPendingAttachments;

    public bool IsReadingAttachments => ChatPage.IsReadingAttachments;

    public bool IsSpeechInputAvailable => ChatPage.IsSpeechInputAvailable;

    public bool IsSpeechListening => ChatPage.IsSpeechListening;

    public string SendButtonToolTip => ChatPage.SendButtonToolTip;

    public Task StartSpeechInputAsync() => ChatPage.StartSpeechInputAsync();

    public Task StopSpeechInputAsync() => ChatPage.StopSpeechInputAsync();

    public IAsyncRelayCommand SendCommand => ChatPage.SendCommand;

    public Task<bool> SendComputerUseAsync(string prompt) => ChatPage.SendComputerUseAsync(prompt);

    public IRelayCommand StopCommand => ChatPage.StopCommand;

    public IAsyncRelayCommand SelectImagesCommand => ChatPage.SelectImagesCommand;

    public IAsyncRelayCommand SelectAttachmentsCommand => ChatPage.SelectAttachmentsCommand;

    public IRelayCommand RemovePendingImageCommand => ChatPage.RemovePendingImageCommand;

    public IRelayCommand RemovePendingDocumentCommand => ChatPage.RemovePendingDocumentCommand;

    public IRelayCommand RemoveQueuedTurnCommand => ChatPage.RemoveQueuedTurnCommand;
    public IRelayCommand BeginEditQueuedTurnCommand => ChatPage.BeginEditQueuedTurnCommand;
    public IRelayCommand SaveQueuedTurnCommand => ChatPage.SaveQueuedTurnCommand;
    public IRelayCommand CancelEditQueuedTurnCommand => ChatPage.CancelEditQueuedTurnCommand;
    public IAsyncRelayCommand AddImagesToQueuedTurnCommand => ChatPage.AddImagesToQueuedTurnCommand;
    public IRelayCommand RemoveQueuedTurnImageCommand => ChatPage.RemoveQueuedTurnImageCommand;

    public Task AddImagesToQueuedTurnAsync(QueuedTurnViewModel item) =>
        ChatPage.AddImagesToQueuedTurnCommand.ExecuteAsync(item);

    [RelayCommand]
    private void TogglePlusMenu()
    {
        IsPlusMenuOpen = !IsPlusMenuOpen;
        if (IsPlusMenuOpen)
        {
            ComposerHarness.IsModePickerOpen = false;
        }
    }

    [RelayCommand]
    private async Task PlusSelectImagesAsync()
    {
        IsPlusMenuOpen = false;
        if (SelectAttachmentsCommand.CanExecute(null))
        {
            await SelectAttachmentsCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    public Task AddPendingFromFilePathsAsync(IEnumerable<string> filePaths) =>
        ChatPage.AddPendingFromFilePathsAsync(filePaths);

    public string SettingsStatus
    {
        get => Settings.SettingsStatus;
        set => Settings.SettingsStatus = value;
    }





    private void UpdateDisplayedBusyState() => ChatPage.UpdateDisplayedBusyState();

    private void StopSession(string sessionId)
    {
        _sessionTurns.TurnHost.Cancel(sessionId);
        _sessionTurns.QueuedTurnPresenter.Clear(sessionId);
    }



    public void UpdateComposerCompletion(string composerText, int caretIndex)
    {
        _composerCaretIndex = caretIndex;
        ChatPage.UpdateComposerCompletion(composerText, caretIndex);
    }

    public void SetComposerMultiLine(bool isMultiLine) => IsComposerMultiLine = isMultiLine;

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

    private void ApplySessionWorkspace() =>
        _ = ApplySessionWorkspaceAsync();

    private async Task ApplySessionWorkspaceAsync()
    {
        SyncWorkspaceContext();
        if (_workspaceContext.Kind == WorkspaceKind.Ssh
            && !string.IsNullOrWhiteSpace(_session.ActiveWorkspaceId))
        {
            try
            {
                await _sshConnection.SyncAsync(_session, _appSettings).ConfigureAwait(true);
            }
            catch
            {
                // Connection errors surface when tools run or when configuring workspace.
            }
        }

        ActiveWorkspaceName = ResolveActiveWorkspaceName();
        // Deliberately NOT reloadSkills: a session switch does not change the installed skills, and
        // the rescan walks every skill folder on the UI thread (measured ~8-10s). Skill changes have
        // their own handler (OnSkillConfigurationChanged), which already reloads the catalog.
        using (SessionSwitchProfiler.Measure(SessionSwitchPhases.SkillReload))
        {
            RefreshAtCompletionSources();
        }

        using (SessionSwitchProfiler.Measure(SessionSwitchPhases.WorkspaceTree))
        {
            await RefreshWorkspaceTreeAsync().ConfigureAwait(true);
        }

        ConfigureWorkspaceWatcher();
        // MCP stdio servers start with the workspace root as their cwd; refresh so a
        // workspace/session change re-evaluates (and reconnects when the root actually moved).
        // Started here, then awaited OUTSIDE the switch's measurement window: this can block on the
        // MCP refresh lock or on reconnecting stdio servers (measured ~9s), none of which belongs to
        // the switch. Tracking the task keeps it alive and surfaces failures via the guard.
        var mcpRefresh = RefreshMcpRuntimeAsync();
        _ = RunGuardedAsync(() => mcpRefresh, "MCP runtime refresh (session switch)");

        OnPropertyChanged(nameof(Sidebar));
        OnPropertyChanged(nameof(HasSessionWorkspace));
        OnPropertyChanged(nameof(WorkspacePanelActionLabel));
        OnPropertyChanged(nameof(RunOnDisplayName));
    }

    private async Task RefreshWorkspaceTreeAsync()
    {
        if (_workspaceContext.Kind == WorkspaceKind.Ssh
            && _sshClient.IsConnected
            && !string.IsNullOrWhiteSpace(_workspaceContext.RootPath))
        {
            try
            {
                var entries = new List<SshEntry>();
                await foreach (var entry in _sshClient.ListAsync(_workspaceContext.RootPath).ConfigureAwait(true))
                {
                    if (SshWorkspaceToolHelper.ShouldIgnore(entry.FullPath, _workspaceContext.IgnorePatterns))
                    {
                        continue;
                    }

                    entries.Add(entry);
                    if (entries.Count >= 200)
                    {
                        break;
                    }
                }

                Sidebar.RefreshRemoteWorkspaceTree(
                    _workspaceContext.RootPath,
                    _workspaceContext.DisplayName ?? _workspaceContext.RootPath,
                    entries,
                    _workspaceContext.IgnorePatterns);
                return;
            }
            catch
            {
                Sidebar.RefreshWorkspaceTree(null, _workspaceContext.IgnorePatterns);
                return;
            }
        }

        Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
    }

    private void SyncWorkspaceContext() =>
        _workspaceBridge.SyncWorkspaceContext(_session, _appSettings, _workspaceContext);

    private void RequestRefreshSessionHistory() =>
        _sessionHistory.RequestRefresh(RefreshSessionHistoryAsync);

    private async Task RefreshSessionHistoryAsync()
    {
        await _sessionHistory.RefreshAsync(_displayedSessionId, _sessionTurns.TurnHost.IsRunning, StopSession);
        OnPropertyChanged(nameof(HasAgentRecords));
    }

    public bool HasAgentRecords => _sessionHistory.HasAgentRecords;


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
        try
        {
            await _sshConnection.DisconnectAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // ignore disconnect errors during shutdown
        }

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
        AppCultureManager.CultureChanged -= OnCultureChanged;
        _sessionTurns.TurnHost.TurnCompleted -= OnTurnCompleted;
        _sessionTurns.TurnHost.TurnStateChanged -= OnTurnStateChanged;
        KnowledgePageVm.KnowledgeDataChanged -= OnKnowledgeDataChanged;
        _taskListChangedNotifier.TaskListChanged -= OnTaskListChanged;
        _composer.AtCompletionSourcesUpdated -= OnAtCompletionSourcesUpdated;
        if (_onMcpConfigurationChanged is not null)
        {
            Settings.McpConfigurationChanged -= _onMcpConfigurationChanged;
        }

        if (_onSkillConfigurationChanged is not null)
        {
            Settings.SkillConfigurationChanged -= _onSkillConfigurationChanged;
        }

        if (_onSettingsSaved is not null)
        {
            Settings.SettingsSaved -= _onSettingsSaved;
        }

        _sessionTurns.QueuedTurnPresenter.QueueChanged -= OnQueuedTurnsChanged;
        if (_savedChatView is not null)
        {
            _savedChatView.OlderMessagesRequested -= OnOlderMessagesRequested;
            _savedChatView.ExternalLinkRequested -= OnChatExternalLinkRequested;
            _savedChatView.ToolDetailRequested -= OnToolDetailRequested;
            _savedChatView.PlanBuildRequested -= OnPlanBuildRequested;
            _savedChatView.PlanReviseRequested -= OnPlanReviseRequested;
        }

        _activeUi.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        WorkspacePane.PropertyChanged -= OnWorkspacePanePropertyChanged;
        StatusFeedback.CancelPendingHide();
        UpdateBanner.Dispose();
        _compactionCts?.Cancel();
        _compactionCts?.Dispose();
        _layout.Dispose();
        _sessionHistory.Dispose();
        _workspaceBridge.Dispose();
        _runtime.Dispose();
    }

    partial void OnCurrentPageChanged(AppPage value)
    {
        CurrentPageView = _pageViewFactory.GetOrCreate(value);
        _navigation.HandlePageChanged(
            value.ToPageKey(),
            Settings,
            SchedulePageVm,
            KnowledgePageVm,
            SkillHubVm);
    }

    private async Task RunGuardedAsync(Func<Task> action, string operation)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"{operation} failed: {ex.Message}");
        }
    }

    private void OnSkillConfigurationChanged()
    {
        _skillCatalog.Reload();
        Sidebar.Refresh(_appSettings);
        // The catalog was just reloaded above; passing reloadSkills again would walk every skill
        // folder a second time in the same handler.
        RefreshAtCompletionSources();
        OnPropertyChanged(nameof(Sidebar));
    }

    partial void OnIsBusyChanged(bool value)
    {
        ClearContextCommand.NotifyCanExecuteChanged();
        CompactContextCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsComposerStopVisible));
        RefreshComputerUseStatus();
    }


    private void RefreshAtCompletionSources(bool reloadSkills = false) =>
        _composer.RefreshSources(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns, reloadSkills);

    private ComposerSlashCommandContext CreateSlashCommandContext() =>
        new()
        {
            Session = _session,
            IsBusy = IsBusy,
            IsCompacting = IsCompacting,
            MessageCount = Messages.Count,
            CompactAsync = cancellationToken => _compactionService.CompactAsync(_session, cancellationToken),
            ClearContextAsync = ClearContextAsync,
            SetStatus = status => ShowShellToast(status, ShellToastKind.Info),
            NotifyCommandStatesChanged = NotifyCommandStatesChanged
        };

    public void ShowCopyNotice(string message) =>
        ShowShellToast(message, ShellToastKind.Success);

    public void ShowShellToast(string message, ShellToastKind kind = ShellToastKind.Info) =>
        StatusFeedback.ShowToast(message, kind);

    public void StartUpdatePolling() => UpdateBanner.Start();

    public void StopUpdatePolling() => UpdateBanner.Stop();

    public void SetComposerStatus(string? message) =>
        StatusFeedback.SetComposerStatus(message);

    /// <summary>
    /// Backs out of plan revision mode (the card's "Revise" button) when it is active. Returns
    /// false otherwise so Escape keeps its normal behavior in the composer.
    /// </summary>
    public bool CancelPlanRevise() => PlanBar.CancelReviseMode();
}

public sealed record AtCompletionItemViewModel(
    string Type,
    string PrimaryText,
    string SecondaryText,
    string InsertText,
    string MatchText,
    ComposerCompletionItemKind Kind = ComposerCompletionItemKind.File,
    string? SlashCommandName = null,
    WorkspaceFileIconKind? IconKind = null);

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
