using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.App.Services;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IFileStorageService _storage;
    private readonly ICredentialStore _credentialStore;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly IMcpRegistry _mcpRegistry;
    private readonly AppSettings _appSettings;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly ISkillRuntime _skillRuntime;
    private readonly IImageAttachmentReader _imageAttachmentReader;
    private readonly IImageAttachmentStore _imageAttachmentStore;
    private readonly IAppPathProvider _paths;
    private readonly SessionTurnHost _turnHost;
    private readonly SessionUiCache _uiCache;
    private readonly ApplicationShutdownService _shutdownService;
    private readonly ISpeechDictationService _speechDictation;
    private readonly SpeechDictationAvailability _speechDictationAvailability;
    private readonly Dictionary<string, ObservableCollection<QueuedTurnViewModel>> _queuedTurnsBySession = new(StringComparer.Ordinal);
    private string _dictationBaseText = string.Empty;
    private string _dictationCommittedText = string.Empty;
    private string _dictationInterimText = string.Empty;
    private bool _isUpdatingComposerFromDictation;
    private FileSystemWatcher? _workspaceWatcher;
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
        SessionUiCache uiCache,
        WorkspaceFileEditorService workspaceFileEditorService,
        ApplicationShutdownService shutdownService,
        ISpeechDictationService speechDictation,
        SpeechDictationAvailability speechDictationAvailability,
        AppSettings settings)
    {
        _storage = storage;
        _credentialStore = credentialStore;
        _workspaceContext = workspaceContext;
        _mcpRegistry = mcpRegistry;
        _imageAttachmentReader = imageAttachmentReader;
        _imageAttachmentStore = imageAttachmentStore;
        _paths = paths;
        _turnHost = turnHost;
        _uiCache = uiCache;
        _shutdownService = shutdownService;
        _speechDictation = speechDictation;
        _speechDictationAvailability = speechDictationAvailability;
        _speechDictation.InterimText += OnDictationInterimText;
        _speechDictation.FinalText += OnDictationFinalText;
        _speechDictation.Error += OnDictationError;
        _appSettings = settings;
        _skillCatalog = skillCatalog;
        _skillRuntime = skillRuntime;
        _displayedSessionId = _session.Id;
        _activeUi = _uiCache.GetOrCreate(_displayedSessionId, RequestScrollToBottom);
        _turnHost.TurnCompleted += OnTurnCompleted;
        _turnHost.TurnStateChanged += OnTurnStateChanged;
        Settings = new SettingsViewModel(settings, _mcpRegistry, skillCatalog, paths);
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
        _appSettings.Ui.ContextSidebarWidth = Math.Clamp(
            _appSettings.Ui.ContextSidebarWidth,
            ContextSidebarMinWidth,
            ContextSidebarMaxWidth);
        if (_appSettings.Ui.ContextSidebarWidth < ContextSidebarMinWidth)
        {
            _appSettings.Ui.ContextSidebarWidth = ContextSidebarDefaultWidth;
        }

        _appSettings.Ui.NavigationSidebarWidth = Math.Clamp(
            _appSettings.Ui.NavigationSidebarWidth,
            NavigationSidebarMinWidth,
            NavigationSidebarMaxWidth);
        if (_appSettings.Ui.NavigationSidebarWidth < NavigationSidebarMinWidth)
        {
            _appSettings.Ui.NavigationSidebarWidth = NavigationSidebarDefaultWidth;
        }

        _appSettings.Ui.EditorPaneWidth = Math.Clamp(
            _appSettings.Ui.EditorPaneWidth,
            EditorPaneMinWidth,
            EditorPaneMaxWidth);
        if (_appSettings.Ui.EditorPaneWidth < EditorPaneMinWidth)
        {
            _appSettings.Ui.EditorPaneWidth = EditorPaneDefaultWidth;
        }

        _appSettings.Ui.ComposerHeight = Math.Clamp(
            _appSettings.Ui.ComposerHeight,
            ComposerMinHeight,
            ComposerMaxHeight);
        if (_appSettings.Ui.ComposerHeight < ComposerMinHeight)
        {
            _appSettings.Ui.ComposerHeight = ComposerDefaultHeight;
        }

        LogsPath = paths.LogsPath;

        ApplySessionWorkspace();
        _activeUi.Messages.CollectionChanged += OnMessagesCollectionChanged;
        PendingImageAttachments.CollectionChanged += OnPendingImagesChanged;
        _ = InitializeAsync();
    }

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
        await RefreshDictationAvailabilityAsync();

        await RefreshSessionHistoryAsync();
        var latest = GetFirstAgentRecord();
        if (latest is not null && _session.Messages.Count == 0)
        {
            await LoadSessionInternalAsync(latest.Id);
        }
    }

    public ObservableCollection<ChatMessageViewModel> Messages => _activeUi.Messages;

    /// <summary>由 MainWindow 注入，用于流式输出时自动滚到底部。</summary>
    public Action? ScrollChatToBottom { get; set; }
    public ObservableCollection<AgentRecordGroupViewModel> AgentRecordGroups { get; } = new();
    public ObservableCollection<QueuedTurnViewModel> QueuedTurns => GetQueuedTurnsFor(_displayedSessionId);
    public bool HasQueuedTurns => QueuedTurns.Count > 0;
    public ContextSidebarViewModel Sidebar { get; }
    public SettingsViewModel Settings { get; }
    public FileEditorViewModel FileEditor { get; }
    public string LogsPath { get; }

    public bool HasOpenEditorTabs => FileEditor.HasOpenTabs;

    public const double ContextSidebarMinWidth = 220;
    public const double ContextSidebarMaxWidth = 560;
    public const double ContextSidebarDefaultWidth = 300;

    public const double NavigationSidebarMinWidth = 180;
    public const double NavigationSidebarMaxWidth = 480;
    public const double NavigationSidebarDefaultWidth = 220;

    public const double EditorPaneMinWidth = 280;
    public const double EditorPaneMaxWidth = 1200;
    public const double EditorPaneDefaultWidth = 480;

    public const double ComposerMinHeight = 120;
    public const double ComposerMaxHeight = 420;
    public const double ComposerDefaultHeight = 168;

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

    public const double ContextSidebarCollapseDragThreshold = 200;

    private CancellationTokenSource? _copyNoticeCts;
    private CancellationTokenSource? _uiLayoutSaveCts;

    [ObservableProperty]
    private string copyNotice = string.Empty;

    [ObservableProperty]
    private bool isCopyNoticeVisible;

    [ObservableProperty]
    private string composerText = string.Empty;

    [ObservableProperty]
    private bool isDictationListening;

    [ObservableProperty]
    private string dictationStatusText = string.Empty;

    public bool IsComposerEmpty => string.IsNullOrWhiteSpace(ComposerText);

    public bool IsDictationAvailable =>
        _appSettings.Speech.Enabled && _speechDictationAvailability.IsAvailable;

    public string DictationToolTip =>
        !_appSettings.Speech.Enabled
            ? "语音听写已在设置中关闭"
            : !IsDictationAvailable
                ? _speechDictationAvailability.UnavailableReason
                : IsDictationListening
                    ? "正在听写，点击结束"
                    : "听写输入（离线）";

    public string ComposerHintText =>
        IsDictationListening
            ? "正在听写… 点击麦克风结束"
            : string.IsNullOrWhiteSpace(DictationStatusText)
                ? "Enter 发送 · Shift+Enter 换行"
                : DictationStatusText;

    [ObservableProperty]
    private string streamingText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string shutdownStatusText = "正在关闭…";

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

    [ObservableProperty]
    private bool isAtCompletionOpen;

    [ObservableProperty]
    private int selectedAtCompletionIndex = -1;

    private readonly List<AtCompletionItemViewModel> _fileCompletionIndex = new();
    private readonly List<AtCompletionItemViewModel> _skillCompletionIndex = new();
    private const int MaxCompletionItems = 30;

    public ObservableCollection<AtCompletionItemViewModel> AtCompletionItems { get; } = new();
    public ObservableCollection<PendingImageAttachmentViewModel> PendingImageAttachments { get; } = new();
    public bool HasPendingImages => PendingImageAttachments.Count > 0;

    public bool IsChatPage => CurrentPage == "Chat";
    public bool IsSettingsPage => CurrentPage == "Settings";

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
    private async Task ToggleContextSidebarAsync()
    {
        SetContextSidebarVisible(!_appSettings.Ui.ContextSidebarVisible);
        await PersistUiLayoutAsync();
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

        var clamped = Math.Clamp(width, ContextSidebarMinWidth, ContextSidebarMaxWidth);
        if (Math.Abs(_appSettings.Ui.ContextSidebarWidth - clamped) < 0.5)
        {
            return;
        }

        _appSettings.Ui.ContextSidebarWidth = clamped;
        SchedulePersistUiLayout();
    }

    public void UpdateNavigationSidebarWidth(double width)
    {
        var clamped = Math.Clamp(width, NavigationSidebarMinWidth, NavigationSidebarMaxWidth);
        if (Math.Abs(_appSettings.Ui.NavigationSidebarWidth - clamped) < 0.5)
        {
            return;
        }

        _appSettings.Ui.NavigationSidebarWidth = clamped;
        SchedulePersistUiLayout();
    }

    public void UpdateComposerHeight(double height)
    {
        var clamped = Math.Clamp(height, ComposerMinHeight, ComposerMaxHeight);
        if (Math.Abs(_appSettings.Ui.ComposerHeight - clamped) < 0.5)
        {
            return;
        }

        _appSettings.Ui.ComposerHeight = clamped;
        SchedulePersistUiLayout();
    }

    private void NotifyContextSidebarLayoutChanged()
    {
        OnPropertyChanged(nameof(IsContextSidebarVisible));
        OnPropertyChanged(nameof(ContextSidebarWidth));
        OnPropertyChanged(nameof(ContextSidebarToggleToolTip));
        ContextSidebarLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task PersistUiLayoutAsync() => _storage.SaveSettingsAsync(_appSettings);

    public Task PersistUiLayoutForSidebarAsync() => PersistUiLayoutAsync();

    private void SchedulePersistUiLayout()
    {
        _uiLayoutSaveCts?.Cancel();
        _uiLayoutSaveCts?.Dispose();
        _uiLayoutSaveCts = new CancellationTokenSource();
        var token = _uiLayoutSaveCts.Token;
        _ = PersistUiLayoutDebouncedAsync(token);
    }

    private async Task PersistUiLayoutDebouncedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(400, cancellationToken);
            await _storage.SaveSettingsAsync(_appSettings);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer width change.
        }
    }

    [RelayCommand]
    private async Task NewSession()
    {
        await SaveCurrentSessionIfNeededAsync();
        if (IsDictationListening || _speechDictation.IsListening)
        {
            await StopDictationInternalAsync(CancellationToken.None);
        }

        _session = AgentSession.Create("New Chat");
        SwitchDisplayedSession(_session);
        CurrentSessionTitle = _session.Title;
        ComposerText = string.Empty;
        PendingImageAttachments.Clear();
        StreamingText = string.Empty;
        UpdateDisplayedBusyState();
        CurrentPage = "Chat";
        ApplySessionWorkspace();
        await RefreshSessionHistoryAsync();
        NotifyCommandStatesChanged();
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
        StreamingText = string.Empty;
        PendingImageAttachments.Clear();

        await _storage.SaveSessionAsync(_session);
        SettingsStatus = "上下文已清空。";
        NotifyCommandStatesChanged();
    }

    private bool CanClearContext() => Messages.Count > 0 && !IsBusy;

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasChatMessages));
        ClearContextCommand.NotifyCanExecuteChanged();
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

        if (IsDictationListening || _speechDictation.IsListening)
        {
            await StopDictationInternalAsync(CancellationToken.None);
        }

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

        if (_turnHost.IsRunning(item.Id))
        {
            _turnHost.Cancel(item.Id);
        }

        _turnHost.ClearQueue(item.Id);
        _queuedTurnsBySession.Remove(item.Id);

        _uiCache.Remove(item.Id);
        await _storage.DeleteSessionAsync(item.Id);

        if (string.Equals(_session.Id, item.Id, StringComparison.Ordinal))
        {
            _session = AgentSession.Create("New Chat");
            SwitchDisplayedSession(_session);
            CurrentSessionTitle = _session.Title;
            ComposerText = string.Empty;
            PendingImageAttachments.Clear();
            StreamingText = string.Empty;
            ApplySessionWorkspace();
            CurrentPage = "Chat";
        }

        await RefreshSessionHistoryAsync();
        SettingsStatus = "对话已删除。";
        NotifyCommandStatesChanged();
    }

    [RelayCommand(CanExecute = nameof(CanToggleDictation))]
    private async Task ToggleDictationAsync()
    {
        if (IsDictationListening)
        {
            await StopDictationAsync();
            return;
        }

        await StartDictationAsync();
    }

    private bool CanToggleDictation() => IsDictationAvailable && !IsBusy;

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
    private Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(ComposerText) && PendingImageAttachments.Count == 0)
        {
            return Task.CompletedTask;
        }

        _skillCatalog.Reload();
        var input = SkillComposerExpander.Expand(ComposerText.Trim(), _skillRuntime.GetSkills());
        var imageAttachments = PersistPendingImages(_displayedSessionId);
        ComposerText = string.Empty;
        StreamingText = string.Empty;
        SyncWorkspaceContext();

        var ui = _uiCache.GetOrCreate(_displayedSessionId, RequestScrollToBottom);
        PendingImageAttachments.Clear();

        if (_turnHost.IsRunning(_displayedSessionId))
        {
            var queueId = Guid.NewGuid().ToString("N");
            _turnHost.Enqueue(new QueuedTurnPayload(queueId, _displayedSessionId, input, imageAttachments, ui));
            GetQueuedTurnsFor(_displayedSessionId).Add(
                QueuedTurnViewModel.Create(queueId, input, imageAttachments));
            NotifyQueuedTurnsChanged();
            SettingsStatus = "已加入排队";
            NotifyCommandStatesChanged();
            return Task.CompletedTask;
        }

        ui.AddUserMessage(input, imageAttachments);
        var request = new SessionTurnRequest(_displayedSessionId, _session, input, imageAttachments, ui, IsAutoContinue: false);
        if (!_turnHost.TryStart(request, out var error))
        {
            SettingsStatus = error ?? "无法开始生成。";
            NotifyCommandStatesChanged();
            return Task.CompletedTask;
        }

        UpdateDisplayedBusyState();
        NotifyCommandStatesChanged();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void RemoveQueuedTurn(QueuedTurnViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (_turnHost.Remove(_displayedSessionId, item.QueueId))
        {
            GetQueuedTurnsFor(_displayedSessionId).Remove(item);
            NotifyQueuedTurnsChanged();
        }
    }

    private void RequestScrollToBottom() => ScrollChatToBottom?.Invoke();

    private void SwitchDisplayedSession(AgentSession session)
    {
        _activeUi.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        _displayedSessionId = session.Id;
        _session = session;
        _activeUi = _uiCache.GetOrCreate(_displayedSessionId, RequestScrollToBottom);
        _activeUi.Messages.CollectionChanged += OnMessagesCollectionChanged;
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(HasChatMessages));
        NotifyQueuedTurnsChanged();
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

            _ = RefreshSessionHistoryAsync();
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

            await SaveCurrentSessionIfNeededAsync(
                string.Equals(e.SessionId, _displayedSessionId, StringComparison.Ordinal)
                    ? _session
                    : e.Session);
            await RefreshSessionHistoryAsync();
            TryProcessNextQueuedTurn(e);
            NotifyCommandStatesChanged();
        });
    }

    private bool TryProcessNextQueuedTurn(SessionTurnCompletedEventArgs e)
    {
        if (!_turnHost.TryDequeue(e.SessionId, out var payload) || payload is null)
        {
            return false;
        }

        RemoveQueuedTurnFromUi(e.SessionId, payload.QueueId);
        payload.Ui.AddUserMessage(payload.UserInput, payload.ImageAttachments);
        var request = new SessionTurnRequest(
            e.SessionId,
            e.Session,
            payload.UserInput,
            payload.ImageAttachments,
            payload.Ui,
            IsAutoContinue: false);

        if (_turnHost.TryStart(request, out var error))
        {
            if (string.Equals(e.SessionId, _displayedSessionId, StringComparison.Ordinal))
            {
                UpdateDisplayedBusyState();
            }

            return true;
        }

        _turnHost.RequeueFront(payload);
        GetQueuedTurnsFor(e.SessionId).Insert(
            0,
            QueuedTurnViewModel.Create(payload.QueueId, payload.UserInput, payload.ImageAttachments));
        NotifyQueuedTurnsChanged();
        if (string.Equals(e.SessionId, _displayedSessionId, StringComparison.Ordinal))
        {
            SettingsStatus = error ?? "无法开始下一条排队消息。";
        }

        return true;
    }

    private void UpdateDisplayedBusyState()
    {
        IsBusy = _turnHost.IsRunning(_displayedSessionId);
    }

    private void StopSession(string sessionId)
    {
        _turnHost.Cancel(sessionId);
        _turnHost.ClearQueue(sessionId);
        ClearQueuedTurnsUi(sessionId);
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
        await RefreshDictationAvailabilityAsync();
        ApplySessionWorkspace();
        OnPropertyChanged(nameof(Sidebar));
        SettingsStatus = $"Saved at {AppTimeZone.Now:HH:mm:ss}";
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

    public void UpdateEditorPaneWidth(double width)
    {
        var clamped = Math.Clamp(width, EditorPaneMinWidth, EditorPaneMaxWidth);
        if (Math.Abs(_appSettings.Ui.EditorPaneWidth - clamped) < 0.5)
        {
            return;
        }

        _appSettings.Ui.EditorPaneWidth = clamped;
        SchedulePersistUiLayout();
    }

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
        && TryGetActiveWorkspaceRoot(out var root)
        && IsPathUnderWorkspace(root, node.FullPath)
        && !IsWorkspaceRootPath(root, node.FullPath);

    private bool TryGetActiveWorkspaceRoot(out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(_session.ActiveWorkspace) || !Directory.Exists(_session.ActiveWorkspace))
        {
            return false;
        }

        root = Path.GetFullPath(_session.ActiveWorkspace);
        return true;
    }

    private static bool IsWorkspaceRootPath(string workspaceRoot, string targetPath)
    {
        var root = NormalizeDirectoryPath(workspaceRoot);
        var target = NormalizeDirectoryPath(targetPath);
        return string.Equals(root, target, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathUnderWorkspace(string workspaceRoot, string targetPath)
    {
        var root = NormalizeDirectoryPath(workspaceRoot);
        var target = Path.GetFullPath(targetPath);
        if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootPrefix = root + Path.DirectorySeparatorChar;
        return target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private bool CanSend() =>
        !string.IsNullOrWhiteSpace(ComposerText) || PendingImageAttachments.Count > 0;

    private ObservableCollection<QueuedTurnViewModel> GetQueuedTurnsFor(string sessionId)
    {
        if (!_queuedTurnsBySession.TryGetValue(sessionId, out var collection))
        {
            collection = new ObservableCollection<QueuedTurnViewModel>();
            _queuedTurnsBySession[sessionId] = collection;
        }

        return collection;
    }

    private void RemoveQueuedTurnFromUi(string sessionId, string queueId)
    {
        var collection = GetQueuedTurnsFor(sessionId);
        var item = collection.FirstOrDefault(turn => string.Equals(turn.QueueId, queueId, StringComparison.Ordinal));
        if (item is not null)
        {
            collection.Remove(item);
        }

        if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
        {
            NotifyQueuedTurnsChanged();
        }
    }

    private void ClearQueuedTurnsUi(string sessionId)
    {
        GetQueuedTurnsFor(sessionId).Clear();
        if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
        {
            NotifyQueuedTurnsChanged();
        }
    }

    private void NotifyQueuedTurnsChanged()
    {
        OnPropertyChanged(nameof(QueuedTurns));
        OnPropertyChanged(nameof(HasQueuedTurns));
    }

    public void UpdateAtCompletion(string composerText, int caretIndex)
    {
        if (!TryGetAtQuery(composerText, caretIndex, out var query))
        {
            CloseAtCompletion();
            return;
        }

        var sorted = _fileCompletionIndex
            .Concat(_skillCompletionIndex)
            .Where(item => MatchesQuery(item.MatchText, query))
            .OrderBy(item => Rank(item.MatchText, query))
            .ThenBy(item => item.PrimaryText, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompletionItems)
            .ToArray();

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
            || !TryGetAtQuerySpan(ComposerText, caretIndex, out var atStart, out var atEndExclusive))
        {
            return false;
        }

        var replacement = AtCompletionItems[SelectedAtCompletionIndex].InsertText;
        if (!replacement.EndsWith(' '))
        {
            replacement += " ";
        }
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
        RefreshAtCompletionSources();
        Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
        ConfigureWorkspaceWatcher();
        OnPropertyChanged(nameof(Sidebar));
    }

    private void SyncWorkspaceContext()
    {
        if (!string.IsNullOrWhiteSpace(_session.ActiveWorkspace))
        {
            var activeRoot = Path.GetFullPath(_session.ActiveWorkspace);
            var match = _appSettings.Workspaces.FirstOrDefault(workspace =>
                !string.IsNullOrWhiteSpace(workspace.RootPath)
                && string.Equals(Path.GetFullPath(workspace.RootPath), activeRoot, StringComparison.OrdinalIgnoreCase));
            _workspaceContext.SetWorkspace(activeRoot, match?.Name, ResolveIgnorePatterns(match));
            return;
        }

        var configured = _appSettings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
        if (configured is null)
        {
            _workspaceContext.SetWorkspace(null);
            return;
        }

        _workspaceContext.SetWorkspace(configured.RootPath, configured.Name, ResolveIgnorePatterns(configured));
    }

    private IReadOnlyList<string> ResolveIgnorePatterns(WorkspaceSettings? workspace) =>
        WorkspaceIgnoreResolver.Resolve(
            workspacePatterns: workspace?.IgnorePatterns,
            globalPatterns: _appSettings.WorkspaceIgnore.DirectoryNames);

    private async Task SaveCurrentSessionIfNeededAsync() =>
        await SaveCurrentSessionIfNeededAsync(_session);

    private async Task SaveCurrentSessionIfNeededAsync(AgentSession session)
    {
        if (session.Messages.Count == 0)
        {
            return;
        }

        var toSave = DeriveSessionTitle(session);
        if (string.Equals(toSave.Id, _displayedSessionId, StringComparison.Ordinal))
        {
            _session = toSave;
        }

        await _storage.SaveSessionAsync(toSave);
        await RefreshSessionHistoryAsync();
    }

    private async Task RefreshSessionHistoryAsync()
    {
        var entries = await _storage.ListSessionsAsync();
        AgentRecordGroups.Clear();
        foreach (var group in AgentRecordGrouping.Build(
                     entries,
                     _session.Id,
                     _turnHost.IsRunning,
                     StopSession))
        {
            if (group.Items.Count > 0)
            {
                AgentRecordGroups.Add(group);
            }
        }

        OnPropertyChanged(nameof(HasAgentRecords));
    }

    public bool HasAgentRecords => AgentRecordGroups.Count > 0;

    private SessionHistoryItemViewModel? GetFirstAgentRecord()
    {
        foreach (var group in AgentRecordGroups)
        {
            if (group.Items.Count > 0)
            {
                return group.Items[0];
            }
        }

        return null;
    }

    private async Task LoadSessionInternalAsync(string sessionId)
    {
        var loaded = await _storage.LoadSessionAsync(sessionId);
        if (loaded is null)
        {
            SettingsStatus = "无法加载该对话。";
            return;
        }

        SwitchDisplayedSession(loaded);
        CurrentSessionTitle = _session.Title;
        ComposerText = string.Empty;
        PendingImageAttachments.Clear();
        StreamingText = string.Empty;

        if (_activeUi.Messages.Count == 0)
        {
            if (_session.Messages.Count > 0)
            {
                _activeUi.HydrateFromSession(_session);
            }
            else
            {
                var displayMessages = await _storage.LoadConversationDisplayAsync(sessionId);
                if (displayMessages.Count > 0)
                {
                    _activeUi.HydrateDisplay(_session, displayMessages);
                }
            }
        }

        ApplySessionWorkspace();
        UpdateDisplayedBusyState();
        SettingsStatus = $"已加载对话：{_session.Title}";
        NotifyCommandStatesChanged();
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
        {
            if (!string.IsNullOrWhiteSpace(e.FullPath))
            {
                FileEditor.HandleExternalFileChange(e.FullPath);
            }

            RefreshAtCompletionSources();
            Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
        });
    }

    public bool HasPendingShutdownWork => _turnHost.HasActiveWork;

    public async Task ShutdownAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _uiLayoutSaveCts?.Cancel();
        _workspaceWatcher?.Dispose();
        _workspaceWatcher = null;

        if (IsDictationListening || _speechDictation.IsListening)
        {
            await StopDictationAsync(cancellationToken).ConfigureAwait(false);
        }

        await _shutdownService.ShutdownAsync(progress, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Legacy entry point; prefer <see cref="ShutdownAsync"/>.</summary>
    public void PrepareForShutdown()
    {
        ShutdownAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _turnHost.TurnCompleted -= OnTurnCompleted;
        _turnHost.TurnStateChanged -= OnTurnStateChanged;
        _speechDictation.InterimText -= OnDictationInterimText;
        _speechDictation.FinalText -= OnDictationFinalText;
        _speechDictation.Error -= OnDictationError;
        if (_speechDictation is IDisposable disposableDictation)
        {
            disposableDictation.Dispose();
        }

        PrepareForShutdown();
        _activeUi.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        _copyNoticeCts?.Cancel();
        _copyNoticeCts?.Dispose();
        _uiLayoutSaveCts?.Cancel();
        _uiLayoutSaveCts?.Dispose();
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
        if (string.Equals(value, "Settings", StringComparison.Ordinal))
        {
            Settings.SyncSkillsFromCatalog();
        }
    }

    private void OnSkillConfigurationChanged()
    {
        _skillCatalog.Reload();
        Sidebar.Refresh(_appSettings);
        RefreshAtCompletionSources();
        OnPropertyChanged(nameof(Sidebar));
    }

    partial void OnIsBusyChanged(bool value)
    {
        ClearContextCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        ToggleDictationCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDictationListeningChanged(bool value)
    {
        OnPropertyChanged(nameof(DictationToolTip));
        OnPropertyChanged(nameof(ComposerHintText));
        ToggleDictationCommand.NotifyCanExecuteChanged();
    }

    partial void OnDictationStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(ComposerHintText));

    partial void OnComposerTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsComposerEmpty));
        SendCommand.NotifyCanExecuteChanged();

        if (_isUpdatingComposerFromDictation || !IsDictationListening)
        {
            return;
        }

        _ = StopDictationAsync();
    }

    private async Task RefreshDictationAvailabilityAsync()
    {
        await _speechDictationAvailability.InitializeAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsDictationAvailable));
        OnPropertyChanged(nameof(DictationToolTip));
        ToggleDictationCommand.NotifyCanExecuteChanged();
    }

    private async Task StartDictationAsync()
    {
        DictationStatusText = string.Empty;
        _dictationBaseText = ComposerText;
        _dictationCommittedText = string.Empty;
        _dictationInterimText = string.Empty;
        IsDictationListening = true;

        await _speechDictation
            .StartAsync(_speechDictationAvailability.EffectiveLanguageTag)
            .ConfigureAwait(true);
        if (!_speechDictation.IsListening)
        {
            IsDictationListening = false;
            ResetDictationBuffers();
        }
    }

    private Task StopDictationAsync(CancellationToken cancellationToken = default) =>
        StopDictationInternalAsync(cancellationToken);

    private async Task StopDictationInternalAsync(CancellationToken cancellationToken)
    {
        if (IsDictationListening || _speechDictation.IsListening)
        {
            await _speechDictation.StopAsync(cancellationToken).ConfigureAwait(true);
        }

        IsDictationListening = false;
        ResetDictationBuffers();
        ToggleDictationCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DictationToolTip));
    }

    private void OnDictationInterimText(object? sender, string text) =>
        RunOnUiThread(() => ApplyDictationInterim(text));

    private void OnDictationFinalText(object? sender, string text) =>
        RunOnUiThread(() => ApplyDictationFinal(text));

    private void OnDictationError(object? sender, string message) =>
        RunOnUiThread(() =>
        {
            DictationStatusText = message;
            _ = StopDictationInternalAsync(CancellationToken.None);
        });

    private void ApplyDictationInterim(string text)
    {
        if (!IsDictationListening)
        {
            return;
        }

        _dictationInterimText = text;
        ApplyDictationComposerText();
    }

    private void ApplyDictationFinal(string text)
    {
        if (!IsDictationListening)
        {
            return;
        }

        _dictationCommittedText = SpeechDictationTextMerger.AppendFinalSegment(_dictationCommittedText, text);
        _dictationInterimText = string.Empty;
        ApplyDictationComposerText();
    }

    private void ApplyDictationComposerText()
    {
        _isUpdatingComposerFromDictation = true;
        try
        {
            ComposerText = SpeechDictationTextMerger.Compose(
                _dictationBaseText,
                _dictationCommittedText,
                _dictationInterimText);
        }
        finally
        {
            _isUpdatingComposerFromDictation = false;
        }
    }

    private void ResetDictationBuffers()
    {
        _dictationBaseText = string.Empty;
        _dictationCommittedText = string.Empty;
        _dictationInterimText = string.Empty;
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.InvokeAsync(action);
    }

    private void RefreshAtCompletionSources()
    {
        _fileCompletionIndex.Clear();
        _skillCompletionIndex.Clear();

        _skillCatalog.Reload();
        foreach (var skill in _skillCatalog.Skills)
        {
            _skillCompletionIndex.Add(new AtCompletionItemViewModel(
                Type: "技能",
                PrimaryText: skill.Name,
                SecondaryText: skill.SkillId,
                InsertText: $"@skill:{skill.SkillId}",
                MatchText: $"{skill.Name} {skill.SkillId}"));
        }

        if (string.IsNullOrWhiteSpace(_session.ActiveWorkspace) || !Directory.Exists(_session.ActiveWorkspace))
        {
            return;
        }

        var root = Path.GetFullPath(_session.ActiveWorkspace);
        var ignoredNames = new HashSet<string>(_workspaceContext.IgnorePatterns, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (ignoredNames.Contains(Path.GetFileName(path)))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                _fileCompletionIndex.Add(new AtCompletionItemViewModel(
                    Type: "文件",
                    PrimaryText: Path.GetFileName(path),
                    SecondaryText: relative,
                    InsertText: $"@{relative}",
                    MatchText: $"{relative} {Path.GetFileName(path)}"));
            }
        }
        catch
        {
            // Keep whatever was indexed successfully.
        }
    }

    private static bool TryGetAtQuery(string text, int caretIndex, out string query)
    {
        query = string.Empty;
        if (!TryGetAtQuerySpan(text, caretIndex, out var atStart, out var atEndExclusive))
        {
            return false;
        }

        query = text[(atStart + 1)..atEndExclusive];
        return true;
    }

    private static bool TryGetAtQuerySpan(string text, int caretIndex, out int atStart, out int atEndExclusive)
    {
        atStart = -1;
        atEndExclusive = -1;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var safeCaret = Math.Clamp(caretIndex, 0, text.Length);
        var index = safeCaret - 1;
        while (index >= 0)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c))
            {
                break;
            }

            if (c == '@')
            {
                atStart = index;
                atEndExclusive = safeCaret;
                return true;
            }

            index--;
        }

        return false;
    }

    private static bool MatchesQuery(string haystack, string query) =>
        string.IsNullOrWhiteSpace(query) || haystack.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static int Rank(string haystack, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        return haystack.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
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

public sealed class PendingImageAttachmentViewModel(ImageAttachment attachment)
{
    public ImageAttachment Attachment { get; } = attachment;
    public string FileName => attachment.FileName;
    public string MimeType => attachment.MimeType;
    public System.Windows.Media.ImageSource? Thumbnail =>
        Services.ImageAttachmentUi.TryCreateThumbnail(attachment);
}
