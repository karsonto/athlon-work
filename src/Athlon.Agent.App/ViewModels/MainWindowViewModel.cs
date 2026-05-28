using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(10);

    private readonly IAgentOrchestrator _orchestrator;
    private readonly IFileStorageService _storage;
    private readonly ICredentialStore _credentialStore;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly IMcpRegistry _mcpRegistry;
    private readonly AppSettings _appSettings;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly IImageAttachmentReader _imageAttachmentReader;
    private FileSystemWatcher? _workspaceWatcher;
    private CancellationTokenSource? _turnCancellation;
    private CancellationTokenSource? _turnTimeoutCancellation;
    private CancellationTokenSource? _turnLinkedCancellation;
    private AgentSession _session = AgentSession.Create("New Chat");
    private ChatMessageViewModel? _streamingAssistantMessage;
    private readonly StringBuilder _streamingTokenBuffer = new();
    private readonly StringBuilder _streamingReasoningBuffer = new();
    private DispatcherTimer? _streamingFlushTimer;

    public MainWindowViewModel(
        IAgentOrchestrator orchestrator,
        IFileStorageService storage,
        ICredentialStore credentialStore,
        IActiveWorkspaceContext workspaceContext,
        IMcpRegistry mcpRegistry,
        IImageAttachmentReader imageAttachmentReader,
        IAppPathProvider paths,
        IAgentSkillCatalog skillCatalog,
        AppSettings settings)
    {
        _orchestrator = orchestrator;
        _storage = storage;
        _credentialStore = credentialStore;
        _workspaceContext = workspaceContext;
        _mcpRegistry = mcpRegistry;
        _imageAttachmentReader = imageAttachmentReader;
        _appSettings = settings;
        _skillCatalog = skillCatalog;
        Settings = new SettingsViewModel(settings, _mcpRegistry);
        Settings.McpConfigurationChanged += async (_, _) => await RefreshMcpRuntimeAsync();
        Sidebar = new ContextSidebarViewModel(paths, skillCatalog, _mcpRegistry, settings);
        HasStoredApiKey = EnsureCurrentApiKeySecret(settings);
        ApplySessionWorkspace();
        Messages.CollectionChanged += OnMessagesCollectionChanged;
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
    }

    [RelayCommand]
    private async Task NewSession()
    {
        CancelTurn();
        await SaveCurrentSessionIfNeededAsync();
        _session = AgentSession.Create("New Chat");
        CurrentSessionTitle = _session.Title;
        ComposerText = string.Empty;
        PendingImageAttachments.Clear();
        StreamingText = string.Empty;
        IsBusy = false;
        Messages.Clear();
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

        CancelTurn();
        ResetTurnUiState();

        _session = _session.WithMessages(Array.Empty<ChatMessage>());
        Messages.Clear();
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

    private void ResetTurnUiState()
    {
        StopStreamingFlushTimer();
        FlushStreamingTokens();
        _streamingTokenBuffer.Clear();
        _streamingReasoningBuffer.Clear();
        _streamingAssistantMessage = null;
        IsBusy = false;
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

        CancelTurn();
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
            PendingImageAttachments.Clear();
            StreamingText = string.Empty;
            Messages.Clear();
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
        foreach (var image in images)
        {
            if (PendingImageAttachments.Any(existing => string.Equals(existing.DataUrl, image.DataUrl, StringComparison.Ordinal)))
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
        if (string.IsNullOrWhiteSpace(ComposerText) && PendingImageAttachments.Count == 0)
        {
            return;
        }

        var input = ComposerText.Trim();
        var imageAttachments = PendingImageAttachments.Select(item => item.Attachment).ToArray();
        ComposerText = string.Empty;
        IsBusy = true;
        StreamingText = string.Empty;
        BeginTurnCancellation();
        Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, input, imageAttachments: imageAttachments)));
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
                    if (message.Role == MessageRole.User
                        && CompactionMessageContent.IsCompressedPlaceholder(message.Content))
                    {
                        return;
                    }

                    if (message.Role == MessageRole.Compaction)
                    {
                        ClearStreamingAssistantPlaceholder();
                        Messages.Add(new ChatMessageViewModel(message));
                        RequestScrollToBottom();
                        return;
                    }

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
                }),
                OnAssistantReasoningDelta = async token => await RunOnUiAsync(() =>
                {
                    EnsureStreamingAssistantMessage();
                    _streamingReasoningBuffer.Append(token);
                    ScheduleStreamingFlush();
                })
            };

            _session = await Task.Run(
                async () => await _orchestrator.SendAsync(_session, input, imageAttachments, callbacks, GetTurnCancellationToken()).ConfigureAwait(false),
                GetTurnCancellationToken()).ConfigureAwait(true);
            CurrentSessionTitle = _session.Title;
            PendingImageAttachments.Clear();
            await SaveCurrentSessionIfNeededAsync();
        }
        catch (OperationCanceledException)
        {
            var timedOut = IsTurnTimedOut();
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

                var notice = timedOut
                    ? "本回合已超过 10 分钟，已自动停止。"
                    : "生成已停止。";
                Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.System, notice)));
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
            _streamingTokenBuffer.Clear();
            _streamingReasoningBuffer.Clear();
            _streamingAssistantMessage = null;
            StreamingText = string.Empty;
            ReconcilePendingToolsFromSession();
            IsBusy = false;
            DisposeTurnCancellation();
            NotifyCommandStatesChanged();
        }
    }

    private void BeginTurnCancellation()
    {
        DisposeTurnCancellation();
        _turnCancellation = new CancellationTokenSource();
        _turnTimeoutCancellation = new CancellationTokenSource(TurnTimeout);
        _turnLinkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _turnCancellation.Token,
            _turnTimeoutCancellation.Token);
    }

    private CancellationToken GetTurnCancellationToken() =>
        _turnLinkedCancellation?.Token ?? CancellationToken.None;

    private bool IsTurnTimedOut() =>
        _turnTimeoutCancellation is { IsCancellationRequested: true }
        && _turnCancellation is { IsCancellationRequested: false };

    private void CancelTurn() => _turnCancellation?.Cancel();

    private void DisposeTurnCancellation()
    {
        _turnLinkedCancellation?.Dispose();
        _turnTimeoutCancellation?.Dispose();
        _turnCancellation?.Dispose();
        _turnLinkedCancellation = null;
        _turnTimeoutCancellation = null;
        _turnCancellation = null;
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
        _streamingReasoningBuffer.Clear();

        if (_streamingAssistantMessage is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_streamingAssistantMessage.Content)
            && string.IsNullOrWhiteSpace(_streamingAssistantMessage.ReasoningContent))
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
        if (_streamingAssistantMessage is null)
        {
            return;
        }

        var didFlush = false;
        if (_streamingTokenBuffer.Length > 0)
        {
            _streamingAssistantMessage.AppendStreamingToken(_streamingTokenBuffer.ToString());
            _streamingTokenBuffer.Clear();
            didFlush = true;
        }

        if (_streamingReasoningBuffer.Length > 0)
        {
            _streamingAssistantMessage.AppendStreamingReasoningToken(_streamingReasoningBuffer.ToString());
            _streamingReasoningBuffer.Clear();
            didFlush = true;
        }

        if (didFlush)
        {
            RequestScrollToBottom();
        }
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

    /// <summary>Sync any tool cards still marked running with persisted session tool messages.</summary>
    private void ReconcilePendingToolsFromSession()
    {
        foreach (var message in Messages.Where(static message => message.IsToolRunning).ToList())
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                message.MarkToolCancelled();
                continue;
            }

            var completed = _session.Messages.LastOrDefault(sessionMessage =>
                sessionMessage.Role == MessageRole.Tool
                && string.Equals(ExtractToolCallId(sessionMessage.Content), message.ToolCallId, StringComparison.Ordinal));
            if (completed is not null)
            {
                message.ApplyCompletedTool(completed);
            }
        }
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

    private static HashSet<string> BuildAnsweredToolCallIds(IReadOnlyList<ChatMessage> messages)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role != MessageRole.Tool)
            {
                continue;
            }

            var toolCallId = ExtractToolCallId(message.Content);
            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                answered.Add(toolCallId);
            }
        }

        return answered;
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
    private void Stop() => CancelTurn();

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

    public void OpenWorkspaceFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }

    private bool CanSend() => !IsBusy && (!string.IsNullOrWhiteSpace(ComposerText) || PendingImageAttachments.Count > 0);

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
        PendingImageAttachments.Clear();
        StreamingText = string.Empty;
        Messages.Clear();

        var answeredToolCallIds = BuildAnsweredToolCallIds(_session.Messages);

        foreach (var message in _session.Messages)
        {
            if (message.Role == MessageRole.User
                && CompactionMessageContent.IsCompressedPlaceholder(message.Content))
            {
                continue;
            }

            if (ChatMessageViewModel.IsAssistantToolCallsOnly(message))
            {
                continue;
            }

            Messages.Add(new ChatMessageViewModel(message));

            if (message.Role != MessageRole.Assistant)
            {
                continue;
            }

            var pendingCalls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
            if (pendingCalls is null)
            {
                continue;
            }

            foreach (var toolCall in pendingCalls)
            {
                if (answeredToolCallIds.Contains(toolCall.Id))
                {
                    continue;
                }

                var orphanResult = AgentRuntime.FormatToolResult(
                    toolCall,
                    ToolResult.Failure(
                        "工具未完成",
                        "上次对话在工具执行时被中断，或 MCP 超时后子进程未返回。请重启应用并在侧边栏刷新 MCP 后重试。"));
                Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.Tool, orphanResult, message.ParentId)));
                answeredToolCallIds.Add(toolCall.Id);
            }
        }

        ApplySessionWorkspace();
        await RefreshSessionHistoryAsync();
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
            RefreshAtCompletionSources();
            Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
        });
    }

    /// <summary>Cancel in-flight work and release watchers before process exit.</summary>
    public void PrepareForShutdown()
    {
        try { CancelTurn(); } catch { /* ignore */ }
        _workspaceWatcher?.Dispose();
        _workspaceWatcher = null;
        StopStreamingFlushTimer();
    }

    public void Dispose()
    {
        PrepareForShutdown();
        DisposeTurnCancellation();
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

    partial void OnIsBusyChanged(bool value) => ClearContextCommand.NotifyCanExecuteChanged();

    partial void OnComposerTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsComposerEmpty));
        SendCommand.NotifyCanExecuteChanged();
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
    public string DataUrl => attachment.DataUrl;
}
