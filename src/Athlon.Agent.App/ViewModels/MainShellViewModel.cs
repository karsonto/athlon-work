using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
using Athlon.Agent.Core.Sso;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.SlashCommands;
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
    private readonly SessionCompactionService _compactionService;
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
    private readonly ILocalizationService _loc;
    private readonly IUserNotifier _notifier;
    private readonly SshWorkspaceConnectionService _sshConnection;
    private readonly ICredentialStore _credentialStore;
    private readonly ISshWorkspaceClient _sshClient;
    private readonly ILongTermMemory _longTermMemory;

    private AgentSession _session = AgentSession.Create("New Chat");
    private string _displayedSessionId;
    private SessionTurnUiController _activeUi;
    private bool _shutdownCompleted;
    private bool _disposed;
    private int _sessionLoadGeneration;
    private int _composerCaretIndex;
    private Controls.WebChatView? _savedChatView;
    private ConversationDisplayCursor? _olderDisplayCursor;
    private bool _olderHistoryLoadInProgress;

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
        ScheduleViewModel schedulePageVm,
        ILocalizationService localization,
        IUserNotifier notifier,
        SshWorkspaceConnectionService sshConnection,
        ICredentialStore credentialStore,
        ISshWorkspaceClient sshClient,
        ILongTermMemory longTermMemory)
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
        _shutdownService = shutdownService;
        _sessionHistory = sessionHistory;
        _sessionNavigation = sessionNavigation;
        _sessionUsageAccumulator = sessionUsageAccumulator;
        _pageViewFactory = pageViewFactory;
        _taskListChangedNotifier = taskListChangedNotifier;
        _loc = localization;
        _notifier = notifier;
        _sshConnection = sshConnection;
        _credentialStore = credentialStore;
        _sshClient = sshClient;
        _longTermMemory = longTermMemory;
        _skillCatalog = skillCatalog;
        _appSettings = settings;
        _contextSidebarEdgeGutterWidth = _appSettings.Ui.ContextSidebarVisible ? 12 : 0;
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
        ComposerHarness.OnModeSelected = () => IsPlusMenuOpen = false;
        ChatPage = chatPage;
        ChatPage.Configure(
            () => _displayedSessionId,
            () => _session,
            () => _activeUi,
            status => Settings.SettingsStatus = status,
            NotifyCommandStatesChanged,
            SyncWorkspaceContext,
            busy => IsBusy = busy,
            () => _workspaceContext.IgnorePatterns,
            TryCancelCompaction,
            CreateSlashCommandContext);
        Settings.McpConfigurationChanged += async (_, _) => await RefreshMcpRuntimeAsync();
        Settings.SkillConfigurationChanged += (_, _) => OnSkillConfigurationChanged();
        Settings.SettingsSaved += async (_, _) => await OnSettingsSavedAsync();
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

    public string ModifiedFilesHeader => _loc.Format("Shell_ModifiedFilesHeader", ModifiedFilesCount);

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

    public event EventHandler<ContextSidebarLayoutChangedEventArgs>? ContextSidebarLayoutChanged;

    public double NavigationSidebarWidth =>
        Math.Clamp(_appSettings.Ui.NavigationSidebarWidth, NavigationSidebarMinWidth, NavigationSidebarMaxWidth);

    public double ComposerHeight =>
        Math.Clamp(_appSettings.Ui.ComposerHeight, ComposerMinHeight, ComposerMaxHeight);

    private double _contextSidebarEdgeGutterWidth = 12;
    private bool _contextSidebarLayoutAnimate;

    public bool IsContextSidebarVisible => _appSettings.Ui.ContextSidebarVisible;

    public bool IsNavigationSidebarVisible => _appSettings.Ui.NavigationSidebarVisible;

    public GridLength ContextSidebarEdgeGutterWidth =>
        new GridLength(_contextSidebarEdgeGutterWidth);

    public double ContextSidebarWidth =>
        Math.Clamp(_appSettings.Ui.ContextSidebarWidth, ContextSidebarMinWidth, ContextSidebarMaxWidth);

    public string ContextSidebarToggleToolTip =>
        IsContextSidebarVisible ? _loc["Shell_ContextSidebarClose"] : _loc["Shell_ContextSidebarOpen"];

    public string NavigationSidebarToggleToolTip =>
        IsNavigationSidebarVisible ? _loc["Shell_NavigationSidebarClose"] : _loc["Shell_NavigationSidebarOpen"];

    public const double ContextSidebarCollapseDragThreshold = UiLayoutConstraints.ContextSidebarCollapseDragThreshold;

    private CancellationTokenSource? _copyNoticeCts;
    private CancellationTokenSource? _compactionCts;

    [ObservableProperty]
    private string copyNotice = string.Empty;

    [ObservableProperty]
    private bool isCopyNoticeVisible;

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
                return _loc["Shell_RunOnThisPc"];
            }

            if (_workspaceContext.Kind == WorkspaceKind.Ssh)
            {
                var match = WorkspaceSessionResolver.FindMatch(_session, _appSettings);
                if (match?.Ssh is not null)
                {
                    var user = match.Ssh.Username;
                    var host = match.Ssh.Host;
                    var label = string.IsNullOrWhiteSpace(match.Name)
                        ? (string.IsNullOrWhiteSpace(user) ? host : $"{user}@{host}")
                        : match.Name;
                    return label;
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

    public ObservableCollection<PendingDocumentAttachmentViewModel> PendingDocumentAttachments =>
        ChatPage.PendingDocumentAttachments;

    public bool HasPendingImages => ChatPage.HasPendingImages;

    public bool HasPendingDocuments => ChatPage.HasPendingDocuments;

    public bool HasPendingAttachments => ChatPage.HasPendingAttachments;

    public bool IsReadingAttachments => ChatPage.IsReadingAttachments;

    public IAsyncRelayCommand SendCommand => ChatPage.SendCommand;

    public IRelayCommand StopCommand => ChatPage.StopCommand;

    public IAsyncRelayCommand SelectImagesCommand => ChatPage.SelectImagesCommand;

    public IAsyncRelayCommand SelectAttachmentsCommand => ChatPage.SelectAttachmentsCommand;

    public IRelayCommand RemovePendingImageCommand => ChatPage.RemovePendingImageCommand;

    public IRelayCommand RemovePendingDocumentCommand => ChatPage.RemovePendingDocumentCommand;

    public IRelayCommand RemoveQueuedTurnCommand => ChatPage.RemoveQueuedTurnCommand;

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
        SetContextSidebarVisible(!_appSettings.Ui.ContextSidebarVisible, animate: true);
        await _layout.PersistNowAsync();
    }

    [RelayCommand]
    private async Task ToggleNavigationSidebarAsync()
    {
        SetNavigationSidebarVisible(!_appSettings.Ui.NavigationSidebarVisible);
        await _layout.PersistNowAsync();
    }

    private void OnCultureChanged(object? sender, EventArgs e) => RefreshLocalizedStrings();

    private void RefreshLocalizedStrings()
    {
        ShutdownStatusText = _loc["Shell_ShuttingDown"];
        OnPropertyChanged(nameof(ThemeToggleToolTip));
        OnPropertyChanged(nameof(ModifiedFilesHeader));
        OnPropertyChanged(nameof(ContextSidebarToggleToolTip));
        OnPropertyChanged(nameof(NavigationSidebarToggleToolTip));
        if (string.IsNullOrWhiteSpace(_session.ActiveWorkspace))
        {
            ActiveWorkspaceName = _loc["Shell_NoWorkspace"];
        }

        OnPropertyChanged(nameof(HasSessionWorkspace));
        OnPropertyChanged(nameof(WorkspacePanelActionLabel));
        OnPropertyChanged(nameof(ChatWelcomeTitle));
        OnPropertyChanged(nameof(ChatWelcomeDescription));
        OnPropertyChanged(nameof(SidebarAccountTitle));
        OnPropertyChanged(nameof(SidebarAccountSubtitle));
        OnPropertyChanged(nameof(SsoAvatarInitial));
    }

    partial void OnSsoDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(ChatWelcomeTitle));
        OnPropertyChanged(nameof(SsoAvatarInitial));
        OnPropertyChanged(nameof(SidebarAccountTitle));
        OnPropertyChanged(nameof(SidebarAccountSubtitle));
    }

    partial void OnIsSsoUserVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarAccountTitle));
        OnPropertyChanged(nameof(SidebarAccountSubtitle));
        OnPropertyChanged(nameof(SsoAvatarInitial));
    }

    private void OnAppThemeChanged(object? sender, EventArgs e) =>
        NotifyThemeToggleStateChanged();

    private void NotifyThemeToggleStateChanged()
    {
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(ThemeToggleToolTip));
    }

    public void SetContextSidebarVisible(bool visible, bool animate = false)
    {
        _contextSidebarLayoutAnimate = animate;
        _layout.SetContextSidebarVisible(visible, NotifyContextSidebarLayoutChanged);
    }

    public void SetNavigationSidebarVisible(bool visible)
    {
        _layout.SetNavigationSidebarVisible(visible, NotifyNavigationSidebarLayoutChanged);
    }

    internal void SetContextSidebarEdgeGutterWidth(double width)
    {
        if (Math.Abs(_contextSidebarEdgeGutterWidth - width) < 0.01)
        {
            return;
        }

        _contextSidebarEdgeGutterWidth = width;
        OnPropertyChanged(nameof(ContextSidebarEdgeGutterWidth));
    }

    public void UpdateComposerHeight(double height)
    {
        _layout.UpdateComposerHeight(height);
        OnPropertyChanged(nameof(ComposerHeight));
    }

    public void UpdateContextSidebarWidth(double width) =>
        _layout.UpdateContextSidebarWidth(width);

    public void UpdateNavigationSidebarWidth(double width) =>
        _layout.UpdateNavigationSidebarWidth(width);

    /// <summary>将 WebChatView 绑定到当前活跃 UI 控制器，用于消息的增量渲染。</summary>
    public void AttachChatView(Controls.WebChatView chatView)
    {
        if (_savedChatView is not null)
        {
            _savedChatView.OlderMessagesRequested -= OnOlderMessagesRequested;
        }

        _savedChatView = chatView;
        chatView.OlderMessagesRequested += OnOlderMessagesRequested;
        _uiCache.AttachChatViewToAll(chatView);
        _activeUi.ChatView = chatView;
        _ = _activeUi.ReloadChatViewAsync();
        _ = chatView.SetOlderMessagesAvailableAsync(_olderDisplayCursor is not null);
    }

    private void NotifyContextSidebarLayoutChanged()
    {
        var animate = _contextSidebarLayoutAnimate;
        _contextSidebarLayoutAnimate = false;
        if (!animate)
        {
            SetContextSidebarEdgeGutterWidth(IsContextSidebarVisible ? 12 : 0);
        }

        OnPropertyChanged(nameof(IsContextSidebarVisible));
        OnPropertyChanged(nameof(ContextSidebarWidth));
        OnPropertyChanged(nameof(ContextSidebarToggleToolTip));
        ContextSidebarLayoutChanged?.Invoke(
            this,
            new ContextSidebarLayoutChangedEventArgs { Animate = animate });
    }

    private void NotifyNavigationSidebarLayoutChanged()
    {
        OnPropertyChanged(nameof(IsNavigationSidebarVisible));
        OnPropertyChanged(nameof(NavigationSidebarWidth));
        OnPropertyChanged(nameof(NavigationSidebarToggleToolTip));
    }

    public Task PersistUiLayoutForSidebarAsync() => _layout.PersistNowAsync();

    [RelayCommand]
    private async Task NewSession()
    {
        CancelPendingSessionLoad();
        IsLoadingSession = false;
        var previousSession = _session;
        _session = AgentSession.Create("New Chat");
        _olderDisplayCursor = null;
        SwitchDisplayedSession(_session);
        CurrentSessionTitle = _session.Title;
        ComposerText = string.Empty;
        PendingImageAttachments.Clear();
        PendingDocumentAttachments.Clear();
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
        if (!_notifier.ConfirmYesNo("Shell_ClearContextTitle", "Shell_ClearContextMessage"))
        {
            return;
        }

        if (_sessionTurns.TurnHost.IsRunning(_displayedSessionId))
        {
            _sessionTurns.TurnHost.Cancel(_displayedSessionId);
        }

        _session = _session.WithMessages(Array.Empty<ChatMessage>());
        _olderDisplayCursor = null;
        await _storage.ClearConversationDisplayAsync(_session.Id);
        PendingImageAttachments.Clear();
        PendingDocumentAttachments.Clear();
        await ComposerHarness.ClearTaskPlanAsync();
        _taskListChangedNotifier.Notify(_session.Id);

        await _storage.SaveSessionAsync(_session);
        _sessionNavigation.Invalidate(_session.Id);
        try
        {
            await _longTermMemory.DeleteCurrentSessionMemoryAsync().ConfigureAwait(true);
        }
        catch
        {
            // Clearing chat must succeed even if memory cleanup fails.
        }

        await _activeUi.HydrateFromSessionAsync(_session).ConfigureAwait(true);
        Settings.SettingsStatus = _loc["Shell_ClearContextDone"];
        NotifyCommandStatesChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCompactContext))]
    private async Task CompactContextAsync()
    {
        if (!_notifier.ConfirmYesNo("Shell_CompactContextTitle", "Shell_CompactContextMessage"))
        {
            return;
        }

        _compactionCts?.Cancel();
        _compactionCts?.Dispose();
        _compactionCts = new CancellationTokenSource();
        var compactionToken = _compactionCts.Token;
        IsCompacting = true;
        NotifyComposerCompactionStateChanged();
        _activeUi.BeginManualCompactionBubble();
        Settings.SettingsStatus = _loc["Shell_CompactingContext"];
        NotifyCommandStatesChanged();

        try
        {
            ManualCompactionResult result;
            try
            {
                result = await _compactionService.CompactAsync(_session, compactionToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                _activeUi.CancelManualCompactionBubble();
                Settings.SettingsStatus = _loc["Shell_CompactContextCancelled"];
                return;
            }
            catch (Exception ex)
            {
                _activeUi.DismissManualCompactionBubble();
                Settings.SettingsStatus = ex.Message;
                return;
            }

            if (!result.Compacted)
            {
                _activeUi.DismissManualCompactionBubble();
                Settings.SettingsStatus = _loc["Shell_CompactContextFailed"];
                return;
            }

            _session = result.Session;
            await _storage.ReplaceConversationDisplayAsync(_session.Id, _session.Messages).ConfigureAwait(true);
            _sessionNavigation.Invalidate(_session.Id);
            var audit = _session.Messages.LastOrDefault(message => message.Role == MessageRole.Compaction);
            if (audit is null)
            {
                _activeUi.DismissManualCompactionBubble();
            }
            else
            {
                _activeUi.CompleteManualCompactionBubble(audit, _session.Messages);
            }

            SessionUsageLine = SessionUsageFormatter.Format(_sessionUsageAccumulator.Get(_displayedSessionId));
            Settings.SettingsStatus = _loc["Shell_CompactContextDone"];
        }
        finally
        {
            IsCompacting = false;
            _compactionCts?.Dispose();
            _compactionCts = null;
            NotifyComposerCompactionStateChanged();
            NotifyCommandStatesChanged();
        }
    }

    private bool TryCancelCompaction()
    {
        if (!IsCompacting)
        {
            return false;
        }

        CancelCompaction();
        return true;
    }

    private void CancelCompaction()
    {
        if (!IsCompacting)
        {
            return;
        }

        _compactionCts?.Cancel();
    }

    private void NotifyComposerCompactionStateChanged()
    {
        OnPropertyChanged(nameof(IsComposerStopVisible));
        OnPropertyChanged(nameof(IsComposerSendVisible));
        CompactContextCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    private bool CanCompactContext() => Messages.Count > 0 && !IsBusy && !IsCompacting;

    private bool CanClearContext() => Messages.Count > 0 && !IsBusy;

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasChatMessages));
        ClearContextCommand.NotifyCanExecuteChanged();
        CompactContextCommand.NotifyCanExecuteChanged();

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
        CompactContextCommand.NotifyCanExecuteChanged();
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

        if (!_notifier.ConfirmYesNo("Shell_DeleteConversationTitle", "Shell_DeleteConversationMessage", item.Title))
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

        string? workspaceKey = null;
        try
        {
            var existing = await _storage.LoadSessionAsync(item.Id).ConfigureAwait(true);
            if (existing is not null)
            {
                if (!string.IsNullOrWhiteSpace(existing.ActiveWorkspaceId))
                {
                    workspaceKey = existing.ActiveWorkspaceId;
                }
                else if (!string.IsNullOrWhiteSpace(existing.ActiveWorkspace))
                {
                    workspaceKey = MemoryScopeResolver.HashPath(existing.ActiveWorkspace);
                }
            }
        }
        catch
        {
            // Fall back to active workspace when resolving the deleted session fails.
        }

        try
        {
            await _longTermMemory.DeleteSessionMemoryAsync(workspaceKey, item.Id).ConfigureAwait(true);
        }
        catch
        {
            // Session deletion must succeed even if memory cleanup fails.
        }

        await _storage.DeleteSessionAsync(item.Id);
        _sessionNavigation.Invalidate(item.Id);

        if (string.Equals(_session.Id, item.Id, StringComparison.Ordinal))
        {
            _session = AgentSession.Create("New Chat");
            SwitchDisplayedSession(_session);
            CurrentSessionTitle = _session.Title;
            ComposerText = string.Empty;
            PendingImageAttachments.Clear();
            PendingDocumentAttachments.Clear();
            ApplySessionWorkspace();
            CurrentPage = AppPage.Chat;
        }

        await RefreshSessionHistoryAsync();
        Settings.SettingsStatus = _loc["Shell_DeleteConversationDone"];
        NotifyCommandStatesChanged();
    }

    public void AddPendingImages(IEnumerable<ImageAttachment> images) =>
        ChatPage.AddPendingImages(images);

    private void RequestScrollToBottom() => _chatScroll.ScrollToBottom();

    private void RequestScrollToBottomImmediate() => _chatScroll.ScrollToBottomImmediate();

    private async void OnOlderMessagesRequested(object? sender, EventArgs e)
    {
        if (_olderHistoryLoadInProgress
            || _olderDisplayCursor is not { } cursor
            || _savedChatView is not { } chatView)
        {
            return;
        }

        _olderHistoryLoadInProgress = true;
        var sessionId = _displayedSessionId;
        var loadGeneration = Volatile.Read(ref _sessionLoadGeneration);
        try
        {
            var page = await _sessionNavigation.LoadOlderDisplayPageAsync(
                sessionId,
                cursor,
                cancellationToken: CancellationToken.None).ConfigureAwait(true);
            if (!string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal)
                || !IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            _olderDisplayCursor = page.OlderCursor;
            var viewModels = await Task.Run(() =>
                ChatTimelineHydrator.BuildDisplayMessages(
                    page.Messages,
                    viewModelCache: null,
                    showToolCalls: _activeUi.ShowToolCalls,
                    synthesizeInterruptedToolResults: false)).ConfigureAwait(true);
            await chatView.PrependMessagesAsync(
                viewModels,
                _activeUi.ShowToolCalls,
                _olderDisplayCursor is not null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Loading older chat history failed: {ex.Message}");
            await chatView.SetOlderMessagesAvailableAsync(_olderDisplayCursor is not null).ConfigureAwait(true);
        }
        finally
        {
            _olderHistoryLoadInProgress = false;
        }
    }

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
            _ = _activeUi.ReloadChatViewAsync();
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
    private async Task WorkspacePanelActionAsync()
    {
        // Kept for compatibility; primary entry is the Run on menu.
        await ConfigureLocalWorkspaceAsync().ConfigureAwait(true);
    }

    public ContextMenu BuildRunOnMenu()
    {
        var menu = new ContextMenu
        {
            Style = Application.Current.TryFindResource("RunOnContextMenuStyle") as Style
        };

        var itemStyle = Application.Current.TryFindResource("RunOnMenuItemStyle") as Style;
        var isLocalActive = HasSessionWorkspace && _workspaceContext.Kind == WorkspaceKind.Local;
        var isSshActive = HasSessionWorkspace && _workspaceContext.Kind == WorkspaceKind.Ssh;
        var activeWorkspaceId = _session.ActiveWorkspaceId;

        menu.Items.Add(new MenuItem
        {
            Header = CreateRunOnSectionHeader(_loc["Shell_RunOn"]),
            IsEnabled = false,
            Style = itemStyle
        });

        var thisPc = new MenuItem
        {
            Header = CreateRunOnRowHeader(
                glyph: "\uE7F8",
                text: _loc["Shell_RunOnThisPc"],
                trailing: isLocalActive ? RunOnTrailing.Check : RunOnTrailing.None),
            Style = itemStyle
        };
        thisPc.Click += (_, _) => ScheduleUi(ConfigureLocalWorkspaceAsync);
        menu.Items.Add(thisPc);

        var remote = new MenuItem
        {
            Header = CreateRunOnRowHeader(
                glyph: "\uE7F4",
                text: _loc["Shell_RunOnRemoteConnection"],
                trailing: RunOnTrailing.Chevron),
            Style = itemStyle
        };

        var sshWorkspaces = _appSettings.Workspaces
            .Where(item => item.WorkspaceKind == WorkspaceKind.Ssh && item.Ssh is not null)
            .ToList();

        if (sshWorkspaces.Count == 0)
        {
            remote.Items.Add(new MenuItem
            {
                Header = CreateRunOnEmptyHint(_loc["Shell_RunOnNoRemotes"]),
                IsEnabled = false,
                Style = itemStyle
            });
        }
        else
        {
            foreach (var workspace in sshWorkspaces)
            {
                var selected = isSshActive
                    && !string.IsNullOrWhiteSpace(activeWorkspaceId)
                    && string.Equals(workspace.Id, activeWorkspaceId, StringComparison.OrdinalIgnoreCase);
                var item = new MenuItem
                {
                    Header = CreateSshConnectionRowHeader(workspace, selected),
                    Style = itemStyle,
                    Tag = workspace
                };
                item.Click += (_, _) =>
                {
                    if (item.Tag is WorkspaceSettings configured)
                    {
                        ScheduleUi(() => ApplyConfiguredSshWorkspaceAsync(configured));
                    }
                };
                remote.Items.Add(item);
            }
        }

        remote.Items.Add(new Separator());

        var sshItem = new MenuItem
        {
            Header = CreateRunOnRowHeader(
                glyph: "\uE710",
                text: _loc["Shell_RunOnConnectSshAction"],
                trailing: RunOnTrailing.None),
            Style = itemStyle
        };
        sshItem.Click += (_, _) => ScheduleUi(ConfigureSshWorkspaceAsync);
        remote.Items.Add(sshItem);
        menu.Items.Add(remote);

        if (HasSessionWorkspace)
        {
            menu.Items.Add(new Separator());
            var remove = new MenuItem
            {
                Header = CreateRunOnRowHeader(
                    glyph: "\uE74D",
                    text: _loc["Context_RemoveWorkspace"],
                    trailing: RunOnTrailing.None),
                Style = itemStyle
            };
            remove.Click += (_, _) => ScheduleUi(RemoveSessionWorkspaceAsync);
            menu.Items.Add(remove);
        }

        return menu;
    }

    private async Task DeleteSshConnectionAsync(WorkspaceSettings workspace)
    {
        var label = FormatRemoteLabel(workspace);
        if (!_notifier.ConfirmYesNo("Shell_SshDeleteConnectionTitle", "Shell_SshDeleteConnectionConfirm", label))
        {
            return;
        }

        var isActive = HasSessionWorkspace
            && _workspaceContext.Kind == WorkspaceKind.Ssh
            && !string.IsNullOrWhiteSpace(_session.ActiveWorkspaceId)
            && string.Equals(workspace.Id, _session.ActiveWorkspaceId, StringComparison.OrdinalIgnoreCase);

        if (isActive)
        {
            if (!await FileEditor.TryCloseAllTabsAsync().ConfigureAwait(true))
            {
                return;
            }

            await _sshConnection.DisconnectAsync().ConfigureAwait(true);
            _session = _session.WithWorkspace(null, workspaceId: null);
            await ApplySessionWorkspaceAsync().ConfigureAwait(true);
            await SaveCurrentSessionIfNeededAsync().ConfigureAwait(true);
        }

        _appSettings.Workspaces.RemoveAll(item =>
            string.Equals(item.Id, workspace.Id, StringComparison.OrdinalIgnoreCase));

        try
        {
            await _credentialStore
                .DeleteSecretAsync(SshWorkspaceSettings.PasswordSecretName(workspace.Id))
                .ConfigureAwait(true);
            await _credentialStore
                .DeleteSecretAsync(SshWorkspaceSettings.KeyPassphraseSecretName(workspace.Id))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        await _storage.SaveSettingsAsync(_appSettings).ConfigureAwait(true);
        Settings.SettingsStatus = _loc.Format("Shell_SshConnectionDeleted", label);
        OnPropertyChanged(nameof(RunOnDisplayName));
        OnPropertyChanged(nameof(HasSessionWorkspace));
        OnPropertyChanged(nameof(WorkspacePanelActionLabel));
    }

    private enum RunOnTrailing
    {
        None,
        Check,
        Chevron
    }

    private static UIElement CreateRunOnSectionHeader(string text) =>
        new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryFindBrush("Brush.SubtleText") ?? Brushes.Gray,
            Margin = new Thickness(4, 2, 4, 4)
        };

    private static UIElement CreateRunOnEmptyHint(string text) =>
        new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = TryFindBrush("Brush.SubtleText") ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 10, 8, 10),
            TextWrapping = TextWrapping.Wrap
        };

    private UIElement CreateSshConnectionRowHeader(WorkspaceSettings workspace, bool selected)
    {
        var grid = new Grid { MinWidth = 210 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = "\uE7F4",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.88,
            Foreground = TryFindBrush("Brush.Text") ?? Brushes.Black
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var label = new TextBlock
        {
            Text = FormatRemoteLabel(workspace),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (selected)
        {
            var check = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Margin = new Thickness(4, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.95,
                Foreground = TryFindBrush("Brush.SubtleText") ?? Brushes.Gray
            };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        var deleteButton = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            ToolTip = _loc["Shell_SshDeleteConnection"],
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = "\uE74D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.7,
                Foreground = TryFindBrush("Brush.SubtleText") ?? Brushes.Gray
            }
        };
        deleteButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ScheduleUi(() => DeleteSshConnectionAsync(workspace));
        };
        Grid.SetColumn(deleteButton, 3);
        grid.Children.Add(deleteButton);

        return grid;
    }

    private static UIElement CreateRunOnRowHeader(string glyph, string text, RunOnTrailing trailing)
    {
        var grid = new Grid { MinWidth = 188 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.88,
            Foreground = TryFindBrush("Brush.Text") ?? Brushes.Black
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (trailing != RunOnTrailing.None)
        {
            var trailingGlyph = trailing == RunOnTrailing.Check ? "\uE73E" : "\uE76C";
            var trailingBlock = new TextBlock
            {
                Text = trailingGlyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = trailing == RunOnTrailing.Check ? 12 : 10,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = trailing == RunOnTrailing.Check ? 0.95 : 0.55,
                Foreground = TryFindBrush("Brush.SubtleText") ?? Brushes.Gray
            };
            Grid.SetColumn(trailingBlock, 2);
            grid.Children.Add(trailingBlock);
        }

        return grid;
    }

    private static Brush? TryFindBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;

    private static void ScheduleUi(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _ = action();
            return;
        }

        // Defer until after ContextMenu closes so modal dialogs can take focus.
        _ = dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Surface unexpected UI failures instead of silent no-op.
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private static string FormatRemoteLabel(WorkspaceSettings workspace)
    {
        if (!string.IsNullOrWhiteSpace(workspace.Name))
        {
            return workspace.Name;
        }

        var ssh = workspace.Ssh;
        if (ssh is null)
        {
            return workspace.RootPath;
        }

        return string.IsNullOrWhiteSpace(ssh.Username)
            ? ssh.Host
            : $"{ssh.Username}@{ssh.Host}";
    }

    private async Task ConfigureSshWorkspaceAsync()
    {
        var dialog = new SshConnectWizardWindow(
            _sshConnection,
            _sshClient,
            _credentialStore,
            _notifier,
            _loc)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true || dialog.ResultWorkspace is null)
        {
            return;
        }

        await ApplyConfiguredSshWorkspaceAsync(dialog.ResultWorkspace).ConfigureAwait(true);
    }

    private async Task ApplyConfiguredSshWorkspaceAsync(WorkspaceSettings configured)
    {
        var existing = _appSettings.Workspaces.FindIndex(item =>
            string.Equals(item.Id, configured.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            _appSettings.Workspaces[existing] = configured;
        }
        else
        {
            _appSettings.Workspaces.Add(configured);
        }

        await _storage.SaveSettingsAsync(_appSettings).ConfigureAwait(true);
        try
        {
            await _sshConnection.SyncAsync(
                _session.WithWorkspace(configured.RootPath, configured.Id),
                _appSettings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notifier.WarningText("Common_Prompt", _loc.Format("Shell_SshTestFailed", ex.Message));
            return;
        }

        _session = _session.WithWorkspace(configured.RootPath, configured.Id);
        await ApplySessionWorkspaceAsync().ConfigureAwait(true);
        await SaveCurrentSessionIfNeededAsync().ConfigureAwait(true);
        Settings.SettingsStatus = _loc.Format("Shell_SshConnectedStatus", FormatRemoteLabel(configured));
    }

    [RelayCommand]
    private async Task ConfigureWorkspaceAsync() =>
        await ConfigureLocalWorkspaceAsync().ConfigureAwait(true);

    private async Task ConfigureLocalWorkspaceAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = _loc["Shell_SelectWorkspace"],
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_session.ActiveWorkspace)
            && _workspaceContext.Kind == WorkspaceKind.Local
            && Directory.Exists(_session.ActiveWorkspace))
        {
            dialog.InitialDirectory = _session.ActiveWorkspace;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _sshConnection.DisconnectAsync().ConfigureAwait(true);

        var folderName = new DirectoryInfo(dialog.FolderName).Name;
        _session = _session.WithWorkspace(dialog.FolderName, workspaceId: null);
        await ApplySessionWorkspaceAsync().ConfigureAwait(true);
        await SaveCurrentSessionIfNeededAsync().ConfigureAwait(true);
        Settings.SettingsStatus = _loc.Format("Shell_WorkspaceStatus", folderName);
    }

    private async Task RemoveSessionWorkspaceAsync()
    {
        if (!HasSessionWorkspace)
        {
            return;
        }

        if (!await FileEditor.TryCloseAllTabsAsync().ConfigureAwait(true))
        {
            return;
        }

        await _sshConnection.DisconnectAsync().ConfigureAwait(true);
        _session = _session.WithWorkspace(null, workspaceId: null);
        await ApplySessionWorkspaceAsync().ConfigureAwait(true);
        await SaveCurrentSessionIfNeededAsync().ConfigureAwait(true);
        Settings.SettingsStatus = _loc["Shell_WorkspaceRemoved"];
    }

    private async Task OnSettingsSavedAsync()
    {
        _uiCache.ApplyShowToolCalls();
        await _activeUi.HydrateFromSessionAsync(_session).ConfigureAwait(true);
        await RefreshMcpRuntimeAsync().ConfigureAwait(true);
        ApplySessionWorkspace();
        ComposerKnowledge.NotifyEmbeddingConfigurationChanged();
        CurrentPage = AppPage.Chat;
    }

    public Task OpenWorkspaceFileInEditorAsync(string path) =>
        FileEditor.OpenFileAsync(path, _session.ActiveWorkspace);

    public Task OpenWorkspaceFileForPreviewAsync(string relativeOrFullPath)
    {
        var fullPath = ResolveWorkspaceFilePath(relativeOrFullPath);
        if (fullPath is null)
        {
            _notifier.Info("Shell_CannotPreviewTitle", "Shell_CannotPreviewMessage");
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
            _notifier.Warning("Shell_OpenFolderFailedTitle", "Shell_OpenFolderFailedMessage", exception.Message);
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
        var kind = node.IsDirectory ? _loc["Shell_FolderKind"] : _loc["Shell_FileKind"];
        var messageKey = node.IsDirectory ? "Shell_DeleteFolderMessage" : "Shell_DeleteFileMessage";

        if (!_notifier.ConfirmYesNo("Shell_DeleteNodeTitle", messageKey, node.Name))
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
                Settings.SettingsStatus = _loc["Shell_TargetMissing"];
                Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
                return;
            }

            RefreshAtCompletionSources();
            Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
            Settings.SettingsStatus = _loc.Format("Shell_DeleteSuccess", kind, node.Name);
        }
        catch (Exception exception)
        {
            _notifier.Warning("Shell_DeleteFailedTitle", "Shell_DeleteFailedMessage", node.Name, exception.Message);
            Settings.SettingsStatus = _loc.Format("Shell_DeleteFailedStatus", exception.Message);
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
        else if (_workspaceContext.Kind != WorkspaceKind.Ssh && _sshClient.IsConnected)
        {
            await _sshConnection.DisconnectAsync().ConfigureAwait(true);
        }

        ActiveWorkspaceName = HasSessionWorkspace
            ? _workspaceContext.DisplayName ?? _loc["Shell_NoWorkspace"]
            : _loc["Shell_NoWorkspace"];
        RefreshAtCompletionSources(reloadSkills: true);
        await RefreshWorkspaceTreeAsync().ConfigureAwait(true);
        ConfigureWorkspaceWatcher();
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
                Settings.SettingsStatus = _loc.Format("Shell_SaveConversationFailed", ex.Message));
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
        Settings.SettingsStatus = _loc["Shell_LoadingConversation"];
        try
        {
            var snapshot = await _sessionNavigation.LoadSnapshotAsync(sessionId).ConfigureAwait(true);
            if (!IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            if (snapshot is null)
            {
                Settings.SettingsStatus = _loc["Shell_LoadConversationFailed"];
                return;
            }

            SwitchDisplayedSession(snapshot.Session, renderExistingMessages: false);
            _olderDisplayCursor = snapshot.OlderDisplayCursor;
            CurrentSessionTitle = _session.Title;
            KnowledgePageVm.SetSession(_displayedSessionId);
            ComposerText = string.Empty;
            PendingImageAttachments.Clear();
            PendingDocumentAttachments.Clear();

            if (!IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            if (snapshot.DisplayMessages.Count > 0)
            {
                await _activeUi.HydrateDisplayAsync(
                    _session,
                    snapshot.DisplayMessages,
                    synthesizeInterruptedToolResults: false).ConfigureAwait(true);
            }
            else
            {
                await _activeUi.HydrateFromSessionAsync(_session).ConfigureAwait(true);
            }

            if (_savedChatView is not null)
            {
                await _savedChatView.SetOlderMessagesAvailableAsync(
                    _olderDisplayCursor is not null).ConfigureAwait(true);
            }

            if (!IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            ApplySessionWorkspace();
            UpdateDisplayedBusyState();
            Settings.SettingsStatus = _loc.Format("Shell_LoadConversationDone", _session.Title);

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
            _workspaceContext,
            path => FileEditor.HandleExternalFileChange(path),
            () =>
            {
                RefreshAtCompletionSources();
                _ = RefreshWorkspaceTreeAsync();
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
        try
        {
            await _sshConnection.DisconnectAsync(cancellationToken).ConfigureAwait(false);
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
        if (_savedChatView is not null)
        {
            _savedChatView.OlderMessagesRequested -= OnOlderMessagesRequested;
        }

        _activeUi.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        UnwireModifiedFilesUi(_activeUi);
        _copyNoticeCts?.Cancel();
        _copyNoticeCts?.Dispose();
        _compactionCts?.Cancel();
        _compactionCts?.Dispose();
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
        CompactContextCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsComposerStopVisible));
    }

    partial void OnIsCompactingChanged(bool value) => NotifyComposerCompactionStateChanged();

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
            SetStatus = status => Settings.SettingsStatus = status,
            NotifyCommandStatesChanged = NotifyCommandStatesChanged
        };

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
    string MatchText,
    ComposerCompletionItemKind Kind = ComposerCompletionItemKind.File,
    string? SlashCommandName = null);

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
