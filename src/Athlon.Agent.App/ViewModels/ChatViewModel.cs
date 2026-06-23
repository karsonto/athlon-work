using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.Streaming;
using Athlon.Agent.App.Themes;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly IFileStorageService _storage;
    private readonly ICredentialStore _credentialStore;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly IImageAttachmentReader _imageAttachmentReader;
    private readonly IImageAttachmentStore _imageAttachmentStore;
    private readonly IAppPathProvider _paths;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly ISkillRuntime _skillRuntime;
    private readonly SessionTurnHost _turnHost;
    private readonly QueuedTurnPresenter _queuedTurnPresenter;
    private readonly ComposerAtCompletionService _atCompletion;
    private readonly SessionUiCache _uiCache;
    private readonly ISessionUsageAccumulator _sessionUsageAccumulator;
    private readonly WorkspaceFileEditorService _workspaceFileEditorService;
    private readonly IMcpRegistry _mcpRegistry;
    private readonly AppSettings _appSettings;
    private readonly KnowledgeViewModel _knowledgePageVm;
    private readonly ComposerKnowledgeViewModel _composerKnowledge;
    private readonly WorkspaceSessionBridge _workspaceBridge = new();
    private readonly Dictionary<string, AgentSession> _sessionCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ChatMessage>> _displayCache = new(StringComparer.Ordinal);

    private AgentSession _session = AgentSession.Create("New Chat");
    private string _displayedSessionId;
    private SessionTurnUiController _activeUi;
    private CancellationTokenSource? _copyNoticeCts;

    public ChatViewModel(
        IFileStorageService storage,
        ICredentialStore credentialStore,
        IActiveWorkspaceContext workspaceContext,
        IImageAttachmentReader imageAttachmentReader,
        IImageAttachmentStore imageAttachmentStore,
        IAppPathProvider paths,
        IAgentSkillCatalog skillCatalog,
        ISkillRuntime skillRuntime,
        SessionTurnHost turnHost,
        QueuedTurnPresenter queuedTurnPresenter,
        ComposerAtCompletionService atCompletion,
        SessionUiCache uiCache,
        ISessionUsageAccumulator sessionUsageAccumulator,
        WorkspaceFileEditorService workspaceFileEditorService,
        IMcpRegistry mcpRegistry,
        AppSettings settings,
        IKnowledgeStore knowledgeStore,
        IKnowledgeIndexer knowledgeIndexer,
        IKnowledgeSearchService knowledgeSearchService,
        ISessionKnowledgeState sessionKnowledgeState)
    {
        _storage = storage;
        _credentialStore = credentialStore;
        _workspaceContext = workspaceContext;
        _imageAttachmentReader = imageAttachmentReader;
        _imageAttachmentStore = imageAttachmentStore;
        _paths = paths;
        _skillCatalog = skillCatalog;
        _skillRuntime = skillRuntime;
        _turnHost = turnHost;
        _queuedTurnPresenter = queuedTurnPresenter;
        _queuedTurnPresenter.QueueChanged += OnQueuedTurnsChanged;
        _atCompletion = atCompletion;
        _uiCache = uiCache;
        _sessionUsageAccumulator = sessionUsageAccumulator;
        _workspaceFileEditorService = workspaceFileEditorService;
        _mcpRegistry = mcpRegistry;
        _appSettings = settings;
        _displayedSessionId = _session.Id;
        _activeUi = _uiCache.GetOrCreate(_displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        WireSessionUsageUi(_activeUi);
        _activeUi.SetDisplayed(true);
        _turnHost.TurnCompleted += OnTurnCompleted;
        _turnHost.TurnStateChanged += OnTurnStateChanged;
        OnPropertyChanged(nameof(HasChatMessages));
        _knowledgePageVm = new KnowledgeViewModel(knowledgeStore, knowledgeIndexer, knowledgeSearchService);
        _knowledgePageVm.KnowledgeDataChanged += OnKnowledgeDataChanged;
        _composerKnowledge = new ComposerKnowledgeViewModel(sessionKnowledgeState, knowledgeStore, settings);
        _activeUi.Messages.CollectionChanged += OnMessagesCollectionChanged;
        PendingImageAttachments.CollectionChanged += OnPendingImagesChanged;
        _knowledgePageVm.SetSession(_displayedSessionId);
        _ = _composerKnowledge.LoadForSessionAsync(_displayedSessionId);
    }

    // ===== Observable Properties =====

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
    private string knowledgeEmbeddingApiKey = string.Empty;

    [ObservableProperty]
    private bool hasStoredKnowledgeEmbeddingApiKey;

    [ObservableProperty]
    private string activeWorkspaceName = "No workspace";

    [ObservableProperty]
    private bool isAtCompletionOpen;

    [ObservableProperty]
    private int selectedAtCompletionIndex = -1;

    // ===== Public Properties =====

    public ObservableCollection<ChatMessageViewModel> Messages => _activeUi.Messages;
    public bool HasChatMessages => Messages.Count > 0;
    public ObservableCollection<AtCompletionItemViewModel> AtCompletionItems { get; } = new();
    public ObservableCollection<PendingImageAttachmentViewModel> PendingImageAttachments { get; } = new();
    public bool HasPendingImages => PendingImageAttachments.Count > 0;
    public ObservableCollection<QueuedTurnViewModel> QueuedTurns => _queuedTurnPresenter.GetForSession(_displayedSessionId);
    public bool HasQueuedTurns => QueuedTurns.Count > 0;

    public Action? ScrollChatToBottom { get; set; }
    public Action? ScrollChatToBottomImmediate { get; set; }
    public KnowledgeViewModel KnowledgePageVm => _knowledgePageVm;
    public ComposerKnowledgeViewModel ComposerKnowledge => _composerKnowledge;

    public AgentSession Session => _session;
    public string DisplayedSessionId => _displayedSessionId;
    public SessionTurnUiController ActiveUi => _activeUi;
    public IActiveWorkspaceContext WorkspaceContext => _workspaceContext;
    public ICredentialStore CredentialStore => _credentialStore;
    public IAppPathProvider Paths => _paths;
    public AppSettings AppSettings => _appSettings;
    public IMcpRegistry McpRegistry => _mcpRegistry;
    public ISkillRuntime SkillRuntime => _skillRuntime;
    public WorkspaceFileEditorService FileEditor => _workspaceFileEditorService;

    // === Session History Properties ===
    public ObservableCollection<AgentRecordGroupViewModel> AgentRecordGroups { get; } = new();
    public bool HasAgentRecords => AgentRecordGroups.Count > 0;
    public string ShutdownStatusText { get => string.Empty; set { } }
    public bool HasPendingShutdownWork => _turnHost.HasActiveWork;
    public IAgentSkillCatalog SkillCatalog => _skillCatalog;
    public WorkspaceFileEditorService FileEditorService => _workspaceFileEditorService;

    // ===== Initialize =====

    public async Task InitializeAsync()
    {
        HasStoredKnowledgeEmbeddingApiKey = await _credentialStore
            .HasSecretAsync(KnowledgeEmbeddingSettings.ApiKeySecretName)
            .ConfigureAwait(false);
        _composerKnowledge.SetEmbeddingApiKeyAvailable(HasStoredKnowledgeEmbeddingApiKey);

        HasStoredApiKey = await _credentialStore.HasSecretAsync(ModelSettings.ApiKeySecretName)
            .ConfigureAwait(false);
    }

    // ===== Commands =====

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
        _knowledgePageVm.SetSession(_displayedSessionId);
        _ = _composerKnowledge.LoadForSessionAsync(_displayedSessionId);
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

    [RelayCommand]
    internal async Task LoadSessionAsync(SessionHistoryItemViewModel? item)
    {
        if (item is null || item.Id == _session.Id)
        {
            return;
        }

        var previousSession = _session;
        await LoadSessionInternalAsync(item.Id);
    }

    [RelayCommand]
    internal async Task DeleteSessionAsync(SessionHistoryItemViewModel? item)
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
        }

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

    [RelayCommand]
    private void Stop() => StopSession(_displayedSessionId);

    private void StopSession(string sessionId)
    {
        _turnHost.Cancel(sessionId);
        _queuedTurnPresenter.Clear(sessionId);
    }

    // ===== Helpers =====

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
        OnPropertyChanged(nameof(QueuedTurns));
        OnPropertyChanged(nameof(HasQueuedTurns));
        UpdateDisplayedBusyState();
        _knowledgePageVm.SetSession(_displayedSessionId);
        _ = _composerKnowledge.LoadForSessionAsync(_displayedSessionId);
    }

    private void UpdateDisplayedBusyState()
    {
        IsBusy = _turnHost.IsRunning(_displayedSessionId);
    }

    private void OnTurnStateChanged(object? sender, string sessionId)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
            {
                UpdateDisplayedBusyState();
            }
        });
    }

    private void OnKnowledgeDataChanged()
    {
        _ = _composerKnowledge.LoadForSessionAsync(_displayedSessionId);
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

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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

    private void OnQueuedTurnsChanged(string sessionId)
    {
        if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(QueuedTurns));
            OnPropertyChanged(nameof(HasQueuedTurns));
        }
    }

    private void NotifyCommandStatesChanged()
    {
        SendCommand.NotifyCanExecuteChanged();
        ClearContextCommand.NotifyCanExecuteChanged();
    }

    private bool CanSend() =>
        !string.IsNullOrWhiteSpace(ComposerText) || PendingImageAttachments.Count > 0;

    private void InvalidateSessionCache(string sessionId)
    {
        _sessionCache.Remove(sessionId);
        _displayCache.Remove(sessionId);
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
            _knowledgePageVm.SetSession(_displayedSessionId);
            ComposerText = string.Empty;
            PendingImageAttachments.Clear();

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

            UpdateDisplayedBusyState();
            SettingsStatus = $"已加载对话：{_session.Title}";
        }
        finally
        {
            IsLoadingSession = false;
            NotifyCommandStatesChanged();
        }
    }

    public void UpdateAtCompletion(string composerText, int caretIndex)
    {
        if (!ComposerAtCompletionService.TryGetQuery(composerText, caretIndex, out var query))
        {
            CloseAtCompletion();
            return;
        }

        _atCompletion.EnsureFileIndexBuilt(
            _skillCatalog,
            string.Empty,
            Array.Empty<string>());

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

    public void Dispose()
    {
        _turnHost.TurnCompleted -= OnTurnCompleted;
        _turnHost.TurnStateChanged -= OnTurnStateChanged;
        _knowledgePageVm.KnowledgeDataChanged -= OnKnowledgeDataChanged;
        _activeUi.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        _copyNoticeCts?.Cancel();
        _copyNoticeCts?.Dispose();
    }

    // ===== Public API for MainWindowViewModel delegation =====

    public async Task OpenSessionByIdAsync(string sessionId) => await LoadSessionInternalAsync(sessionId);

    public async Task SaveCurrentSessionIfNeededAsync()
    {
        if (_session.Messages.Count == 0) return;
        var toSave = SessionHistoryCoordinator.DeriveSessionTitle(_session);
        if (string.Equals(toSave.Id, _displayedSessionId, StringComparison.Ordinal))
            _session = toSave;
        await _storage.SaveSessionAsync(toSave);
        InvalidateSessionCache(toSave.Id);
    }

    public void ApplySessionWorkspace()
    {
        SyncWorkspaceContext();
        ActiveWorkspaceName = string.IsNullOrWhiteSpace(_workspaceContext.DisplayName) ? "未配置工作区" : _workspaceContext.DisplayName!;
        RefreshAtCompletionSources(reloadSkills: true);
    }

    public void SyncWorkspaceContext() =>
        _workspaceBridge.SyncWorkspaceContext(_session, _appSettings, _workspaceContext);

    public void SetSessionWorkspace(string folderPath)
    {
        _session = _session.WithWorkspace(folderPath);
    }

    public void RefreshAtCompletionSources(bool reloadSkills = false)
    {
        _atCompletion.RefreshSources(
            _skillCatalog,
            _session.ActiveWorkspace,
            _workspaceContext.IgnorePatterns,
            reloadSkills);
    }

    public void UpdateComposerCompletion(string composerText, int caretIndex)
    {
        if (!ComposerAtCompletionService.TryGetQuery(composerText, caretIndex, out var query))
        {
            CloseAtCompletion();
            return;
        }
        _atCompletion.EnsureFileIndexBuilt(_skillCatalog, _session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
        var sorted = _atCompletion.FilterMatches(query);
        AtCompletionItems.Clear();
        foreach (var item in sorted) AtCompletionItems.Add(item);
        if (AtCompletionItems.Count == 0) { CloseAtCompletion(); return; }
        IsAtCompletionOpen = true;
        if (SelectedAtCompletionIndex < 0 || SelectedAtCompletionIndex >= AtCompletionItems.Count)
            SelectedAtCompletionIndex = 0;
    }

    private async Task SaveSessionInBackgroundAsync(AgentSession session)
    {
        try
        {
            if (session.Messages.Count == 0) return;
            var toSave = SessionHistoryCoordinator.DeriveSessionTitle(session);
            await _storage.SaveSessionAsync(toSave);
            InvalidateSessionCache(toSave.Id);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatViewModel] Save session error: {ex}");
        }
    }

    public void RequestRefreshSessionHistory()
    {
        // Placeholder - session history refresh is managed by MainWindowViewModel through _sessionHistory
    }

    private async Task SaveSessionCoreAsync(AgentSession session)
    {
        if (session.Messages.Count == 0) return;
        var toSave = SessionHistoryCoordinator.DeriveSessionTitle(session);
        if (string.Equals(toSave.Id, _displayedSessionId, StringComparison.Ordinal))
            _session = toSave;
        await _storage.SaveSessionAsync(toSave);
        InvalidateSessionCache(toSave.Id);
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
        Athlon.Agent.App.Services.ImageAttachmentUi.TryCreateThumbnail(Attachment);
}
