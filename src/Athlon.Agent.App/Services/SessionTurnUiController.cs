using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Services.Streaming;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>Mutable session handle shared with turn callbacks so compaction sees the latest messages.</summary>
public sealed class LiveAgentSession
{
    private readonly object _lock = new();
    private AgentSession _value;

    public LiveAgentSession(AgentSession value) => _value = value;

    public AgentSession Value
    {
        get { lock (_lock) return _value; }
        set { lock (_lock) _value = value; }
    }
}

/// <summary>Per-session chat UI state (messages + streaming buffers) for parallel turns.</summary>
public sealed partial class SessionTurnUiController
{
    private static readonly Action NoOpScroll = () => { };
    private const int MaxMessagesInMemory = 200;
    private const int TrimThreshold = 250;

    private readonly Dispatcher _dispatcher;
    private readonly SessionStreamingUiContext _streaming = new();
    private readonly SessionModifiedFilesTracker _modifiedFilesTracker = new();
    private readonly SessionTurnActivityTracker _turnActivityTracker = new();
    /// <summary>
    /// Full transcript (including folded activity tools) used to replay TURN_ACTIVITY / FILES_CHANGED.
    /// <see cref="Messages"/> omits those tools when show-tool-calls is off.
    /// </summary>
    private List<ChatMessage> _activitySourceMessages = new();
    private readonly ToolCallArgsDisplayCoordinator _displayCoordinator = new();
    private readonly StreamingTokenBuffer _tokenBuffer;
    private readonly ConcurrentDictionary<string, PendingUiApproval> _pendingApprovals =
        new(StringComparer.Ordinal);
    // Cache ViewModels by message ID so switching back to a previously-viewed
    // session reuses MarkdownMessageView / FlowDocument instead of rebuilding everything.
    private readonly Dictionary<string, ChatMessageViewModel> _viewModelCache = new(StringComparer.Ordinal);
    private int _bulkChatViewSyncDepth;
    private int _syncChatViewGeneration;
    private Func<bool> _showToolCalls = () => true;
    private readonly HashSet<string> _foldedAssistantMessageIds = new(StringComparer.Ordinal);
    /// <summary>
    /// After the turn has used activity tools, intermediate assistant text streams into the
    /// activity fold instead of flashing as a standalone bubble.
    /// </summary>
    private bool _turnSawActivityTool;

    private Action _requestScroll = NoOpScroll;
    private Action _requestScrollImmediate = NoOpScroll;

    public SessionTurnUiController(
        Dispatcher dispatcher,
        Action? requestScroll = null,
        Action? requestScrollImmediate = null)
    {
        _dispatcher = dispatcher;
        _tokenBuffer = new StreamingTokenBuffer(dispatcher, _streaming);
        _tokenBuffer.FlushTimerTick += (_, _) =>
            FlushStreamingTokens();
        RequestScroll = requestScroll ?? NoOpScroll;
        RequestScrollImmediate = requestScrollImmediate ?? requestScroll ?? NoOpScroll;
        Messages = new ObservableCollection<ChatMessageViewModel>();
        Messages.CollectionChanged += OnMessagesCollectionChanged;
        _streaming.ShowToolCalls = () => _showToolCalls();
    }

    public bool ShowToolCalls => _showToolCalls();

    public void SetShowToolCalls(bool showToolCalls)
    {
        _showToolCalls = () => showToolCalls;
        _streaming.ShowToolCalls = _showToolCalls;
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; }

    public ObservableCollection<ModifiedFileViewModel> ModifiedFiles => _modifiedFilesTracker.ModifiedFiles;

    public bool HasModifiedFiles => _modifiedFilesTracker.HasModifiedFiles;

    public Action RequestScroll
    {
        get => _requestScroll;
        set
        {
            _requestScroll = value ?? NoOpScroll;
            _streaming.RequestScroll = _requestScroll;
        }
    }

    public Action RequestScrollImmediate
    {
        get => _requestScrollImmediate;
        set
        {
            _requestScrollImmediate = value ?? NoOpScroll;
            _streaming.RequestScrollImmediate = _requestScrollImmediate;
        }
    }

    private WebChatView? _chatView;

    /// <summary>WebChatView 实例（由 MainWindow 在初始化后注入），用于增量渲染消息。</summary>
    public WebChatView? ChatView
    {
        get => _chatView;
        set
        {
            if (ReferenceEquals(_chatView, value))
            {
                return;
            }

            if (_chatView is not null)
            {
                _chatView.ToolApprovalDecisionReceived -= OnToolApprovalDecisionReceived;
            }

            _chatView = value;
            if (_chatView is not null)
            {
                _chatView.ToolApprovalDecisionReceived += OnToolApprovalDecisionReceived;
                if (IsDisplayed)
                {
                    ShowPendingApprovals();
                }
            }
        }
    }

    private volatile bool _isDisplayed;

    public bool IsDisplayed => _isDisplayed;

    private bool CanTouchChatView => _isDisplayed && ChatView is not null;

    /// <summary>
    /// Sync/reload may run with a test override when no real <see cref="WebChatView"/> is attached.
    /// Incremental ChatView dispatch still requires <see cref="CanTouchChatView"/>.
    /// </summary>
    private bool CanSyncChatView =>
        _isDisplayed && (ChatView is not null || ReloadChatViewOverride is not null);

    /// <summary>Test seam: replaces <see cref="WebChatView.LoadMessagesAsync"/> during Sync/Reload.</summary>
    internal Func<Task>? ReloadChatViewOverride { get; set; }

    /// <summary>Test seam: transcript used to replay FILES_CHANGED / TURN_ACTIVITY.</summary>
    internal IReadOnlyList<ChatMessage> ActivitySourceMessages => _activitySourceMessages;

    /// <summary>Test seam: activity source sliced to the displayed window for WebView replay.</summary>
    internal IReadOnlyList<ChatMessage> ReplayActivitySource => BuildReplayActivitySource();

    /// <summary>Test seam: generation bumped when a chat-view sync is scheduled.</summary>
    internal int SyncChatViewGeneration => Volatile.Read(ref _syncChatViewGeneration);

    public void SetDisplayed(bool displayed)
    {
        if (_isDisplayed == displayed)
        {
            return;
        }

        _isDisplayed = displayed;
        RunOnUiSync(() =>
        {
            if (displayed)
            {
                FlushBufferedStreamingToUi();
                ShowPendingApprovals();
            }
            else
            {
                _tokenBuffer.StopFlushTimer();
            }
        });
    }

    public Action<SessionUsageSnapshot>? OnUsageRecorded { get; set; }

    public Action<ContextBudgetSnapshot, ContextPressureLevel>? OnContextBudgetUpdated { get; set; }

    public Action? OnOverflowRetrySkipped { get; set; }

    public AgentTurnCallbacks BuildCallbacks(LiveAgentSession? liveSession = null) => new()
    {
        OnSessionUpdated = session =>
        {
            if (liveSession is not null)
            {
                liveSession.Value = session;
            }

            return Task.CompletedTask;
        },
        OnUsageRecorded = snapshot =>
        {
            OnUsageRecorded?.Invoke(snapshot);
            return Task.CompletedTask;
        },
        OnToolApprovalRequested = RequestToolApprovalAsync,
        OnStreamEvent = streamEvent =>
        {
            if (streamEvent is AgentStreamEvent.UsageRecorded(var snapshot))
            {
                OnUsageRecorded?.Invoke(snapshot);
                return Task.CompletedTask;
            }

            if (streamEvent is AgentStreamEvent.ContextBudgetUpdated(var budget, var pressure))
            {
                OnContextBudgetUpdated?.Invoke(budget, pressure);
                return Task.CompletedTask;
            }

            if (streamEvent is AgentStreamEvent.OverflowRetrySkipped)
            {
                OnOverflowRetrySkipped?.Invoke();
            }

            switch (streamEvent)
            {
                case AgentStreamEvent.TextMessageContent(var messageId, var delta):
                    _tokenBuffer.AppendTextToken(messageId, delta);
                    if (IsDisplayed)
                    {
                        _tokenBuffer.ScheduleFlush(IsDisplayed);
                    }

                    return Task.CompletedTask;
                case AgentStreamEvent.ReasoningMessageContent(var messageId, var delta):
                    _tokenBuffer.AppendReasoningToken(messageId, delta);
                    if (IsDisplayed)
                    {
                        _tokenBuffer.ScheduleFlush(IsDisplayed);
                    }

                    return Task.CompletedTask;
                default:
                    if (!IsDisplayed)
                    {
                        _tokenBuffer.EnqueueEvent(streamEvent);
                        RunOnUiSync(() =>
                        {
                            _modifiedFilesTracker.Process(streamEvent);
                            TryAppendActivitySourceFromStreamEvent(streamEvent);
                        });
                        return Task.CompletedTask;
                    }

                    return RunOnUiAsync(() =>
                    {
                        FlushBufferedStreamingToUi();
                        ProcessUiStreamEvents(streamEvent, notifyTracker: true);
                    });
            }
        }
    };

    /// <summary>
    /// Aligns the activity/files replay transcript with <paramref name="session"/> so switching
    /// back to a cached UI can rebuild FILES_CHANGED cards.
    /// </summary>
    public void SyncActivitySourceFromSession(AgentSession session) =>
        RunOnUiSync(() => MergeActivitySourceFromSession(session));

    public async Task ReloadChatViewAsync()
    {
        if (!_isDisplayed)
        {
            return;
        }

        if (ReloadChatViewOverride is not null)
        {
            await ReloadChatViewOverride().ConfigureAwait(true);
            return;
        }

        if (ChatView is null)
        {
            return;
        }

        var chatView = ChatView;
        var activitySource = BuildReplayActivitySource();
        await chatView.LoadMessagesAsync(
                Messages,
                _showToolCalls(),
                activitySource.Count > 0 ? activitySource : null)
            .ConfigureAwait(true);
        if (ReferenceEquals(ChatView, chatView) && IsDisplayed)
        {
            await RestorePendingToolApprovalsAsync().ConfigureAwait(true);
            RestoreLiveTurnCardsAfterReload();
        }
    }

    public void AddUserMessage(string input, IReadOnlyList<ImageAttachment> imageAttachments)
    {
        RunOnUiSync(() =>
        {
            _modifiedFilesTracker.BeginTurn();
            _turnActivityTracker.BeginTurn();
            _turnSawActivityTool = false;
            _foldedAssistantMessageIds.Clear();
            var message = ChatMessage.Create(MessageRole.User, input, imageAttachments: imageAttachments);
            AppendActivitySourceMessage(message);
            Messages.Add(new ChatMessageViewModel(message));
            TrimMessagesIfNeeded();
            RequestScrollImmediate();
        });
    }

    public void ResetForTurn()
    {
        RunOnUiSync(() =>
        {
            _modifiedFilesTracker.BeginTurn();
            _turnActivityTracker.BeginTurn();
            _turnSawActivityTool = false;
            _foldedAssistantMessageIds.Clear();
            _tokenBuffer.ClearBuffers();
            _tokenBuffer.StopFlushTimer();
            _streaming.Reset();
            _displayCoordinator.Reset();
        });
    }

    public void Release()
    {
        foreach (var pending in _pendingApprovals.Values)
        {
            pending.Completion.TrySetCanceled();
        }

        _pendingApprovals.Clear();
        RunOnUiSync(() =>
        {
            ResetForTurn();
            _bulkChatViewSyncDepth++;
            try
            {
                Messages.Clear();
                _viewModelCache.Clear();
                _modifiedFilesTracker.Clear();
                _turnActivityTracker.Clear();
                _activitySourceMessages = new List<ChatMessage>();
            }
            finally
            {
                _bulkChatViewSyncDepth--;
                SyncChatView(immediate: true);
            }
        });
    }

    private Task RunOnUiTaskAsync(Func<Task> action)
    {
        if (_dispatcher.CheckAccess())
        {
            return action();
        }

        return _dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    public SessionTurnEndSnapshot CaptureEndSnapshot(
        AgentSession session,
        bool wasCancelled,
        bool timedOut,
        string? errorMessage)
    {
        SessionTurnEndSnapshot? snapshot = null;
        RunOnUiSync(() =>
        {
            FlushBufferedStreamingToUi();
            FlushStreamingTokens();

            var (pendingTokens, pendingReasoning, _, _) = _tokenBuffer.PeekPending();

            var assistantContent = _streaming.ActiveAssistantBubble?.Content;
            if (pendingTokens.Length > 0)
            {
                assistantContent = (assistantContent ?? string.Empty) + pendingTokens;
            }

            var assistantReasoning = _streaming.ActiveAssistantBubble?.ReasoningContent;
            if (pendingReasoning.Length > 0)
            {
                assistantReasoning = (assistantReasoning ?? string.Empty) + pendingReasoning;
            }

            snapshot = new SessionTurnEndSnapshot(
                string.IsNullOrWhiteSpace(assistantContent) ? null : assistantContent,
                string.IsNullOrWhiteSpace(assistantReasoning) ? null : assistantReasoning,
                CollectIncompleteToolCalls(session),
                wasCancelled,
                timedOut,
                errorMessage);
        });

        return snapshot!;
    }

    public void FinalizeTurn(
        AgentSession session,
        IReadOnlyList<ChatMessage> persistedTurnMessages,
        bool cancelled,
        bool timedOut,
        int turnTimeoutMinutes,
        string? errorMessage = null)
    {
        RunOnUiSync(() =>
        {
            _tokenBuffer.StopFlushTimer();
            FlushBufferedStreamingToUi();
            FlushStreamingTokens();
            _tokenBuffer.ClearBuffers();

            if (cancelled)
            {
                if (_streaming.ActiveAssistantBubble is { } bubble)
                {
                    bubble.MarkStreamingCancelled();
                }

                foreach (var message in Messages.Where(static message => message.IsToolRunning))
                {
                    message.MarkToolCancelled();
                }

                foreach (var message in _streaming.ToolBubblesByIndex.Values.ToList())
                {
                    message.MarkStreamingToolCancelled();
                }

                // Stopped mid-turn: fold remaining progress text into the activity (no orphan bubbles).
                FoldTurnAssistantNarrations(includeAll: true);
            }
            else if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                _streaming.Process(new AgentStreamEvent.ClearEmptyAssistantPlaceholder(), Messages);
                foreach (var message in _streaming.ToolBubblesByIndex.Values.ToList())
                {
                    message.MarkStreamingToolCancelled();
                }

                FoldTurnAssistantNarrations(includeAll: true);
            }

            _streaming.Reset();
            ReconcilePendingToolsFromSession(session);
            MergeActivitySourceFromSession(session);
            // Drop provisional fold text so the final assistant reply is only a bubble.
            _turnActivityTracker.ClearLiveNarration();
            _bulkChatViewSyncDepth++;
            try
            {
                ApplyPersistedTurnMessages(persistedTurnMessages, timedOut, turnTimeoutMinutes, errorMessage);
            }
            finally
            {
                _bulkChatViewSyncDepth--;
                FinalizeStreamingDisplay();
                DispatchCurrentTurnActivity();
                if (IsDisplayed)
                {
                    RequestScrollImmediate();
                }
            }
        });
    }

    private void DispatchCurrentTurnActivity() => SealCurrentSegment();

    /// <summary>
    /// Finalizes the live activity / files bubbles for the whole turn (once),
    /// then starts a fresh accumulator for the next turn.
    /// </summary>
    private void SealCurrentSegment()
    {
        if (!CanTouchChatView)
        {
            // Keep the live segment so switching back can restore one fold with thought.
            // Wiping here drops reasoning and makes the next upsert a second, shorter card.
            _turnActivityTracker.FinishPendingThought();
            return;
        }

        _turnActivityTracker.FinishPendingThought();
        var summary = _turnActivityTracker.Snapshot();
        if (summary is { HasContent: true })
        {
            _ = ChatView!.DispatchTurnActivityAsync(summary, upsert: false);
        }

        var files = _modifiedFilesTracker.TakeAndClearSegmentSucceededFiles();
        if (files.Count > 0)
        {
            _ = ChatView!.DispatchFilesChangedAsync(files, upsert: false);
        }

        _turnActivityTracker.BeginSegment();
    }

    private void PublishTurnActivity(bool upsert = true)
    {
        if (!CanTouchChatView)
        {
            return;
        }

        var summary = _turnActivityTracker.Snapshot();
        if (summary is null || !summary.HasContent)
        {
            return;
        }

        _ = ChatView!.DispatchTurnActivityAsync(summary, upsert: upsert);
    }

    private void PublishFilesChanged(bool upsert = true)
    {
        if (!CanTouchChatView)
        {
            return;
        }

        var files = upsert
            ? _modifiedFilesTracker.TakeCurrentTurnSucceededFiles()
            : _modifiedFilesTracker.TakeAndClearSegmentSucceededFiles();
        if (files.Count == 0)
        {
            return;
        }

        _ = ChatView!.DispatchFilesChangedAsync(files, upsert: upsert);
    }

    private void ProcessUiStreamEvents(AgentStreamEvent streamEvent, bool notifyTracker)
    {
        // Fold provisional assistant text before the next tool is appended to the activity list,
        // so narrations keep timeline order (text → tool → text → tool).
        if (streamEvent is AgentStreamEvent.ToolCallStart(_, var startingToolName, _)
            && TurnActivityClassifier.IsActivityTool(startingToolName))
        {
            _turnActivityTracker.ClearLiveNarration();
            FoldTurnAssistantNarrations(includeAll: true);
            _turnSawActivityTool = true;
        }

        if (notifyTracker)
        {
            _modifiedFilesTracker.Process(streamEvent);
            _turnActivityTracker.Process(streamEvent);
            TryAppendActivitySourceFromStreamEvent(streamEvent);
        }

        foreach (var uiEvent in _displayCoordinator.MapForUi(streamEvent))
        {
            DispatchToChatView(uiEvent);
            _streaming.Process(uiEvent, Messages);
            NotifyChatViewAfterStreamEvent(uiEvent);
        }

        if (streamEvent is AgentStreamEvent.ToolCallStart
            or AgentStreamEvent.ToolCallArgs
            or AgentStreamEvent.ToolCallEnd
            or AgentStreamEvent.ToolCallResult
            or AgentStreamEvent.ReasoningMessageContent
            or AgentStreamEvent.ReasoningMessageEnd)
        {
            PublishTurnActivity(upsert: true);
            if (streamEvent is AgentStreamEvent.ToolCallResult)
            {
                PublishFilesChanged(upsert: true);
            }
        }
    }

    /// <summary>
    /// Moves assistant text bubbles from the current turn into the turn-activity fold.
    /// When <paramref name="includeAll"/> is false, keeps the last assistant bubble as the final reply.
    /// </summary>
    private void FoldTurnAssistantNarrations(bool includeAll)
    {
        var lastUserIndex = -1;
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].IsUser)
            {
                lastUserIndex = i;
                break;
            }
        }

        var assistants = new List<ChatMessageViewModel>();
        for (var i = lastUserIndex + 1; i < Messages.Count; i++)
        {
            var message = Messages[i];
            if (message.IsUser
                || message.IsTool
                || message.IsCompaction
                || message.IsHiddenPlaceholder
                || string.IsNullOrWhiteSpace(message.Content)
                || _foldedAssistantMessageIds.Contains(message.MessageId))
            {
                continue;
            }

            assistants.Add(message);
        }

        if (assistants.Count == 0)
        {
            return;
        }

        var foldCount = includeAll ? assistants.Count : Math.Max(0, assistants.Count - 1);
        if (foldCount == 0)
        {
            return;
        }

        var removedIds = new List<string>(foldCount);
        for (var i = 0; i < foldCount; i++)
        {
            var message = assistants[i];
            _turnActivityTracker.ClearLiveNarration();
            _turnActivityTracker.AddNarration(message.Content);
            _foldedAssistantMessageIds.Add(message.MessageId);
            removedIds.Add(message.MessageId);
        }

        if (CanTouchChatView && removedIds.Count > 0)
        {
            _ = ChatView!.RemoveAssistantBubblesAsync(removedIds);
        }

        PublishTurnActivity(upsert: true);
    }

    public Task HydrateFromSessionAsync(AgentSession session) =>
        RebuildDisplayFromMessagesAsync(session.Messages, synthesizeInterruptedToolResults: true);

    public Task HydrateDisplayAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults = true,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null) =>
        RebuildDisplayFromMessagesAsync(
            displayMessages,
            synthesizeInterruptedToolResults,
            activitySourceMessages);

    /// <summary>
    /// Rebuild the current display page after settings that affect rendering (e.g. show tool calls),
    /// without pulling the full <see cref="AgentSession.Messages"/> into the UI.
    /// </summary>
    public Task RefreshDisplayForSettingsAsync() =>
        RunOnUiAsync(async () =>
        {
            var displayedIds = new HashSet<string>(
                Messages
                    .Where(message => !message.IsHiddenPlaceholder)
                    .Select(message => message.MessageId),
                StringComparer.Ordinal);
            var activity = _activitySourceMessages.ToList();
            var display = activity.Count > 0 && displayedIds.Count > 0
                ? activity.Where(message => displayedIds.Contains(message.Id)).ToList()
                : activity;
            if (display.Count == 0)
            {
                display = activity;
            }

            await RebuildDisplayFromMessagesCoreAsync(
                    display,
                    synthesizeInterruptedToolResults: true,
                    activity)
                .ConfigureAwait(true);
        });

    public void HydrateFromSession(AgentSession session) =>
        RunOnUiSync(() => RebuildDisplayFromMessages(session.Messages, synthesizeInterruptedToolResults: true));

    public void HydrateDisplay(
        AgentSession session,
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults = true,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null) =>
        RunOnUiSync(() => RebuildDisplayFromMessages(
            displayMessages,
            synthesizeInterruptedToolResults,
            activitySourceMessages));

    private Task RebuildDisplayFromMessagesAsync(
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null) =>
        RunOnUiAsync(async () =>
        {
            await RebuildDisplayFromMessagesCoreAsync(
                    displayMessages,
                    synthesizeInterruptedToolResults,
                    activitySourceMessages)
                .ConfigureAwait(true);
        });

    private void RebuildDisplayFromMessages(
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null)
    {
        var viewModels = BeginRebuildDisplay(
            displayMessages,
            synthesizeInterruptedToolResults,
            activitySourceMessages);
        foreach (var viewModel in viewModels)
        {
            Messages.Add(viewModel);
        }

        FinishRebuildDisplay(viewModels);
    }

    private async Task RebuildDisplayFromMessagesCoreAsync(
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null)
    {
        var viewModels = BeginRebuildDisplay(
            displayMessages,
            synthesizeInterruptedToolResults,
            activitySourceMessages);
        const int batchSize = ConversationDisplayLimits.UiHydrateBatchSize;
        for (var i = 0; i < viewModels.Count; i++)
        {
            Messages.Add(viewModels[i]);
            if (i > 0 && i % batchSize == 0)
            {
                await DispatcherYieldAsync().ConfigureAwait(true);
            }
        }

        FinishRebuildDisplay(viewModels);
    }

    private IReadOnlyList<ChatMessageViewModel> BeginRebuildDisplay(
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null)
    {
        _bulkChatViewSyncDepth++;
        Messages.Clear();
        _streaming.Reset();
        _displayCoordinator.Reset();
        _activitySourceMessages = (activitySourceMessages ?? displayMessages).ToList();

        // Prune cache: remove entries that belong to old sessions (not in the new display list)
        var currentIds = new HashSet<string>(displayMessages.Select(m => m.Id), StringComparer.Ordinal);
        var staleKeys = _viewModelCache.Keys.Where(k => !currentIds.Contains(k)).ToList();
        foreach (var key in staleKeys)
        {
            _viewModelCache.Remove(key);
        }

        return ChatTimelineHydrator.BuildDisplayMessages(
            displayMessages,
            _viewModelCache,
            _showToolCalls(),
            synthesizeInterruptedToolResults);
    }

    private void FinishRebuildDisplay(IReadOnlyList<ChatMessageViewModel> viewModels)
    {
        // Cache ViewModels for future session switches
        foreach (var viewModel in viewModels)
        {
            _viewModelCache[viewModel.MessageId] = viewModel;
        }

        TrimMessagesIfNeeded();
        var fileSource = _activitySourceMessages.Count > 0
            ? _activitySourceMessages.Select(message => new ChatMessageViewModel(message)).ToList()
            : viewModels;
        _modifiedFilesTracker.RebuildFromMessages(fileSource);
        _bulkChatViewSyncDepth--;
        SyncChatView(immediate: true);
        RequestScrollImmediate();
    }

    private async Task DispatcherYieldAsync()
    {
        if (!_dispatcher.CheckAccess())
        {
            await Task.Yield();
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, () => tcs.TrySetResult());
        await tcs.Task.ConfigureAwait(true);
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!CanSyncChatView || _bulkChatViewSyncDepth > 0)
        {
            return;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                _modifiedFilesTracker.RebuildFromMessages(Messages);
                SyncChatView();
                break;
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems?.Count == 1 && e.NewItems[0] is ChatMessageViewModel single)
                {
                    if (single.IsUser)
                    {
                        DispatchUserMessageToChatView(single);
                    }
                    else if (single.IsCompaction)
                    {
                        // Only visible (manual) checkpoints split the fold. Hidden auto-compaction
                        // must not seal, or replay+live upsert stacks two activity cards.
                        if (ChatDisplayPolicy.ShouldDisplayCompactionCheckpoint(single))
                        {
                            SealCurrentSegment();
                            if (CanTouchChatView)
                            {
                                _ = ChatView!.ApplyToolResultMarkdownAsync(single);
                            }
                        }
                    }
                    else if (!IsStreamingChatItem(single))
                    {
                        if (ShouldAvoidFullChatReload)
                        {
                            DispatchIncrementalChatItem(single);
                        }
                        else
                        {
                            SyncChatView();
                        }
                    }
                }
                else if (!ShouldAvoidFullChatReload)
                {
                    SyncChatView();
                }

                break;
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (!ShouldAvoidFullChatReload)
                {
                    SyncChatView();
                }

                break;
        }
    }

    /// <summary>
    /// Full WebView reload while a turn still has live FILES_CHANGED paths re-emits a sealed
    /// card from replay, then live upsert creates a second card that repeats those files.
    /// </summary>
    private bool ShouldAvoidFullChatReload =>
        _modifiedFilesTracker.HasCurrentTurnPaths
        || _turnActivityTracker.HasSegmentContent
        || _streaming.ActiveAssistantBubble is not null
        || _streaming.ToolBubblesByIndex.Count > 0;

    private void DispatchIncrementalChatItem(ChatMessageViewModel message)
    {
        if (!CanTouchChatView)
        {
            return;
        }

        if (message.IsTool || message.IsCompaction)
        {
            _ = ChatView!.ApplyToolResultMarkdownAsync(message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Content)
            && !_foldedAssistantMessageIds.Contains(message.MessageId))
        {
            _ = ChatView!.ApplyAssistantMarkdownAsync(message);
        }
    }

    private static bool IsStreamingChatItem(ChatMessageViewModel message) =>
        message.IsStreaming || message.StreamToolIndex is not null;

    private void FinalizeStreamingDisplay()
    {
        if (!IsDisplayed || ChatView is null)
        {
            return;
        }

        var lastUserIndex = -1;
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].IsUser)
            {
                lastUserIndex = i;
                break;
            }
        }

        for (var i = lastUserIndex + 1; i < Messages.Count; i++)
        {
            var message = Messages[i];
            if (message.IsHiddenPlaceholder)
            {
                continue;
            }

            if (message.IsTool || message.IsCompaction)
            {
                var detail = !string.IsNullOrWhiteSpace(message.ToolDetailExpandedDisplay)
                    ? message.ToolDetailExpandedDisplay
                    : !string.IsNullOrWhiteSpace(message.ToolDetail)
                        ? message.ToolDetail
                        : message.ToolSummary;
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    _ = ChatView.ApplyToolResultMarkdownAsync(message);
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.Content)
                && !_foldedAssistantMessageIds.Contains(message.MessageId))
            {
                _ = ChatView.ApplyAssistantMarkdownAsync(message);
            }
        }
    }

    private void SyncChatView(bool immediate = false)
    {
        if (!CanSyncChatView)
        {
            return;
        }

        if (immediate)
        {
            Interlocked.Increment(ref _syncChatViewGeneration);
            _ = ReloadChatViewAsync();
            return;
        }

        var generation = Interlocked.Increment(ref _syncChatViewGeneration);
        _dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation != _syncChatViewGeneration || !CanSyncChatView)
            {
                return;
            }

            _ = ReloadChatViewAsync();
        });
    }

    private void TrimMessagesIfNeeded()
    {
        if (Messages.Count <= TrimThreshold)
            return;

        // 保留最新的 MaxMessagesInMemory 条
        var excess = Messages.Count - MaxMessagesInMemory;
        for (var i = 0; i < excess; i++)
        {
            var removed = Messages[0];
            Messages.RemoveAt(0);
            // 从 ViewModelCache 也移除，但保留 Compact 消息
            if (!removed.IsCompaction)
            {
                _viewModelCache.Remove(removed.MessageId);
            }
        }

        // 在顶部插入一条占位消息，点击可加载更早消息
        if (excess > 0)
        {
            Messages.Insert(0, new ChatMessageViewModel(
                ChatMessage.Create(MessageRole.System, $"<!-- 查看更多历史消息 ({excess} 条已折叠) -->"),
                isFoldedHistoryPlaceholder: true));
        }

        TrimActivitySourceToDisplayedMessages();
    }

    private void TrimActivitySourceToDisplayedMessages()
    {
        if (_activitySourceMessages.Count == 0 || Messages.Count == 0)
        {
            return;
        }

        var firstUserId = Messages.FirstOrDefault(message => message.IsUser)?.MessageId;
        if (string.IsNullOrWhiteSpace(firstUserId))
        {
            return;
        }

        var startIndex = _activitySourceMessages.FindIndex(message =>
            string.Equals(message.Id, firstUserId, StringComparison.Ordinal));
        if (startIndex > 0)
        {
            _activitySourceMessages.RemoveRange(0, startIndex);
        }
    }

    private void AppendActivitySourceMessage(ChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Id))
        {
            return;
        }

        if (message.Role is not (MessageRole.User or MessageRole.Tool or MessageRole.Assistant or MessageRole.Compaction))
        {
            return;
        }

        if (_activitySourceMessages.Any(existing =>
                string.Equals(existing.Id, message.Id, StringComparison.Ordinal)))
        {
            return;
        }

        _activitySourceMessages.Add(message);
    }

    private void TryAppendActivitySourceFromStreamEvent(AgentStreamEvent streamEvent)
    {
        switch (streamEvent)
        {
            case AgentStreamEvent.ToolCallResult(_, var content, var messageId):
                AppendActivitySourceMessage(ChatMessage.CreateWithId(messageId, MessageRole.Tool, content));
                break;
            case AgentStreamEvent.ChatMessageAppended(var message):
                AppendActivitySourceMessage(message);
                break;
        }
    }

    private void RestoreLiveTurnCardsAfterReload()
    {
        PublishFilesChanged(upsert: true);

        var live = _turnActivityTracker.Snapshot();
        if (live is not { HasContent: true } || !CanTouchChatView)
        {
            return;
        }

        var replayed = TurnActivitySummaryBuilder.Build(CurrentTurnActivitySourceViewModels());
        var summary = TurnActivitySummaryBuilder.OverlayLiveThought(replayed, live);
        if (summary.HasContent)
        {
            _ = ChatView!.DispatchTurnActivityAsync(summary, upsert: true);
        }
    }

    private List<ChatMessageViewModel> CurrentTurnActivitySourceViewModels()
    {
        var source = _activitySourceMessages;
        var start = 0;
        for (var i = source.Count - 1; i >= 0; i--)
        {
            if (source[i].Role == MessageRole.User)
            {
                start = i + 1;
                break;
            }

            if (source[i].Role == MessageRole.Compaction
                && ChatDisplayPolicy.ShouldDisplayCompactionCheckpoint(source[i]))
            {
                start = i + 1;
                break;
            }
        }

        var result = new List<ChatMessageViewModel>(Math.Max(0, source.Count - start));
        for (var i = start; i < source.Count; i++)
        {
            result.Add(new ChatMessageViewModel(source[i]));
        }

        return result;
    }

    private List<ChatMessage> BuildReplayActivitySource()
    {
        if (_activitySourceMessages.Count == 0)
        {
            return _activitySourceMessages;
        }

        var firstUserId = Messages.FirstOrDefault(message => message.IsUser)?.MessageId;
        if (string.IsNullOrWhiteSpace(firstUserId))
        {
            return _activitySourceMessages;
        }

        var startIndex = _activitySourceMessages.FindIndex(message =>
            string.Equals(message.Id, firstUserId, StringComparison.Ordinal));
        if (startIndex <= 0)
        {
            return _activitySourceMessages;
        }

        return _activitySourceMessages.GetRange(startIndex, _activitySourceMessages.Count - startIndex);
    }

    private void MergeActivitySourceFromSession(AgentSession session)
    {
        if (session.Messages.Count == 0)
        {
            return;
        }

        int startIndex;
        if (_activitySourceMessages.Count > 0)
        {
            var lastId = _activitySourceMessages[^1].Id;
            var lastIndex = -1;
            for (var i = 0; i < session.Messages.Count; i++)
            {
                if (string.Equals(session.Messages[i].Id, lastId, StringComparison.Ordinal))
                {
                    lastIndex = i;
                    break;
                }
            }

            // Missing last id: keep the existing paged source instead of copying the full transcript.
            startIndex = lastIndex >= 0 ? lastIndex + 1 : session.Messages.Count;
        }
        else
        {
            startIndex = -1;
            var firstDisplayedId = Messages.FirstOrDefault(message =>
                !message.IsHiddenPlaceholder
                && (message.IsUser || message.IsTool || !string.IsNullOrWhiteSpace(message.Content)))
                ?.MessageId;
            if (!string.IsNullOrWhiteSpace(firstDisplayedId))
            {
                for (var i = 0; i < session.Messages.Count; i++)
                {
                    if (string.Equals(session.Messages[i].Id, firstDisplayedId, StringComparison.Ordinal))
                    {
                        startIndex = i;
                        break;
                    }
                }
            }

            if (startIndex < 0)
            {
                return;
            }
        }

        for (var i = startIndex; i < session.Messages.Count; i++)
        {
            AppendActivitySourceMessage(session.Messages[i]);
        }

        var backfill = ConversationActivitySource.CollectTurnStartBackfill(
            session.Messages,
            _activitySourceMessages);
        if (backfill.Count > 0)
        {
            _activitySourceMessages = ConversationActivitySource.PrependOlder(
                backfill,
                _activitySourceMessages);
        }
    }

    private bool ContainsMessageId(string messageId) =>
        !string.IsNullOrWhiteSpace(messageId)
        && Messages.Any(message => string.Equals(message.MessageId, messageId, StringComparison.Ordinal));

    private static bool ShouldHideMessageFromChat(ChatMessage message) =>
        ChatTimelineHydrator.ShouldHideMessageFromChat(message);

    private void FlushBufferedStreamingToUi()
    {
        foreach (var streamEvent in _tokenBuffer.DrainPendingStreamEvents())
        {
            ProcessUiStreamEvents(streamEvent, notifyTracker: false);
        }

        FlushStreamingTokens();
    }

    private void NotifyChatViewAfterStreamEvent(AgentStreamEvent streamEvent)
    {
        if (!IsDisplayed || ChatView is null)
        {
            return;
        }

        if (streamEvent is AgentStreamEvent.TextMessageEnd(var endMessageId))
        {
            var assistant = Messages.LastOrDefault(message =>
                string.Equals(message.MessageId, endMessageId, StringComparison.Ordinal));
            if (assistant is not null
                && !string.IsNullOrWhiteSpace(assistant.Content)
                && !_foldedAssistantMessageIds.Contains(assistant.MessageId))
            {
                if (_turnSawActivityTool)
                {
                    _turnActivityTracker.SetLiveNarration(assistant.Content);
                    PublishTurnActivity(upsert: true);
                }
                else
                {
                    _ = ChatView.ApplyAssistantMarkdownAsync(assistant);
                }
            }

            return;
        }

        if (streamEvent is AgentStreamEvent.ToolCallResult(var toolCallId, _, _))
        {
            var toolMessage = Messages.LastOrDefault(message =>
                message.IsTool
                && string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal));
            if (toolMessage is not null)
            {
                _ = ChatView.ApplyToolResultMarkdownAsync(toolMessage);
            }
        }
    }

    private void FlushStreamingTokens()
    {
        var (pendingTokens, pendingReasoning, textMessageId, reasoningMessageId) = _tokenBuffer.PeekPending();
        _tokenBuffer.FlushTokens(Messages, IsDisplayed, RequestScroll);
        if (!IsDisplayed || ChatView is null)
        {
            return;
        }

        // Reasoning is folded into TURN_ACTIVITY; do not emit standalone purple thought bubbles.
        if (pendingReasoning.Length > 0 && reasoningMessageId is not null)
        {
            _turnActivityTracker.Process(
                new AgentStreamEvent.ReasoningMessageContent(reasoningMessageId, pendingReasoning));
            PublishTurnActivity();
        }

        if (pendingTokens.Length > 0 && textMessageId is not null)
        {
            // Live-render Markdown from the accumulated assistant content (C# Markdig → HTML).
            var assistant = FindAssistantMessage(textMessageId);
            if (assistant is not null
                && !string.IsNullOrWhiteSpace(assistant.Content)
                && !_foldedAssistantMessageIds.Contains(assistant.MessageId))
            {
                if (_turnSawActivityTool)
                {
                    // Keep intermediate replies inside the fold — no outside bubble flash.
                    _turnActivityTracker.SetLiveNarration(assistant.Content);
                    PublishTurnActivity(upsert: true);
                }
                else
                {
                    _ = ChatView.ApplyAssistantMarkdownAsync(assistant, streaming: true);
                }
            }
        }
    }

    private ChatMessageViewModel? FindAssistantMessage(string messageId) =>
        Messages.LastOrDefault(message =>
            !message.IsUser
            && !message.IsTool
            && string.Equals(message.MessageId, messageId, StringComparison.Ordinal));

    private void DispatchToChatView(AgentStreamEvent streamEvent)
    {
        if (!IsDisplayed || ChatView is null || !ShouldDispatchToChatView(streamEvent))
        {
            return;
        }

        _ = ChatView.DispatchEventAsync(streamEvent);
    }

    private void DispatchUserMessageToChatView(ChatMessageViewModel message)
    {
        if (!IsDisplayed || ChatView is null)
        {
            return;
        }

        _ = ChatView.DispatchUserMessageAsync(message);
    }

    private bool ShouldDispatchToChatView(AgentStreamEvent streamEvent)
    {
        if (_turnSawActivityTool
            && streamEvent is AgentStreamEvent.TextMessageStart
                or AgentStreamEvent.TextMessageContent
                or AgentStreamEvent.TextMessageEnd)
        {
            // Intermediate text is rendered inside TURN_ACTIVITY; avoid creating a bubble first.
            return false;
        }

        return streamEvent is not AgentStreamEvent.UsageRecorded
            and not AgentStreamEvent.ContextHygieneApplied
            and not AgentStreamEvent.ContextBudgetUpdated
            and not AgentStreamEvent.ChatMessageAppended
            and not AgentStreamEvent.ClearEmptyAssistantPlaceholder
            and not AgentStreamEvent.ReasoningMessageStart
            and not AgentStreamEvent.ReasoningMessageContent
            and not AgentStreamEvent.ReasoningMessageEnd
        && !TurnActivityClassifier.IsActivityToolStreamEvent(streamEvent, _turnActivityTracker.ResolveToolName)
        && (_showToolCalls() || !ChatDisplayPolicy.IsToolStreamEvent(streamEvent));
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

    private IReadOnlyList<AgentToolCall> CollectIncompleteToolCalls(AgentSession session)
    {
        var answered = ChatTimelineHydrator.BuildAnsweredToolCallIds(session.Messages);
        var incomplete = new Dictionary<string, AgentToolCall>(StringComparer.Ordinal);

        foreach (var message in Messages)
        {
            if (!message.IsTool || string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                continue;
            }

            if (answered.Contains(message.ToolCallId) || incomplete.ContainsKey(message.ToolCallId))
            {
                continue;
            }

            if (message.ToolCallStatus is ToolCallDisplayStatus.Preparing
                or ToolCallDisplayStatus.Running
                or ToolCallDisplayStatus.Cancelled)
            {
                incomplete[message.ToolCallId] = new AgentToolCall(
                    message.ToolCallId,
                    string.IsNullOrWhiteSpace(message.ToolName) ? "unknown" : message.ToolName,
                    new Dictionary<string, string>());
            }
        }

        return incomplete.Values.ToList();
    }

    private void ApplyPersistedTurnMessages(
        IReadOnlyList<ChatMessage> persistedTurnMessages,
        bool timedOut,
        int turnTimeoutMinutes,
        string? errorMessage)
    {
        foreach (var message in persistedTurnMessages)
        {
            if (message.Role == MessageRole.Compaction)
            {
                if (ChatDisplayPolicy.ShouldDisplayCompactionCheckpoint(message))
                {
                    AppendCompactionNotice(message);
                }

                continue;
            }

            if (ShouldHideMessageFromChat(message))
            {
                continue;
            }

            if (message.Role == MessageRole.Tool)
            {
                if (!_showToolCalls())
                {
                    continue;
                }

                var toolCallId = ChatTimelineHydrator.ExtractToolCallId(message.Content);
                var existing = FindToolMessage(toolCallId);
                if (existing is not null)
                {
                    existing.ApplyCompletedTool(message);
                    continue;
                }
            }

            if (message.Role == MessageRole.Assistant)
            {
                if (_streaming.ActiveAssistantBubble is not null
                    && string.Equals(_streaming.ActiveAssistantBubble.MessageId, message.Id, StringComparison.Ordinal))
                {
                    _streaming.ActiveAssistantBubble.CompleteStreamingAssistant(message);
                    continue;
                }

                if (ChatMessageViewModel.IsAssistantToolCallsOnly(message))
                {
                    continue;
                }
            }

            if (!ContainsMessageId(message.Id))
            {
                Messages.Add(new ChatMessageViewModel(message));
            }
        }

        if (persistedTurnMessages.Any(static message => message.Role == MessageRole.System))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.System, errorMessage)));
            return;
        }

        if (timedOut)
        {
            Messages.Add(new ChatMessageViewModel(
                ChatMessage.Create(MessageRole.System, $"本回合已超过 {turnTimeoutMinutes} 分钟，已自动停止。")));
        }
    }

    private void ReconcilePendingToolsFromSession(AgentSession session)
    {
        foreach (var message in Messages.Where(static message => message.IsToolRunning).ToList())
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                message.MarkToolCancelled();
                continue;
            }

            var completed = session.Messages.LastOrDefault(sessionMessage =>
                sessionMessage.Role == MessageRole.Tool
                && string.Equals(ChatTimelineHydrator.ExtractToolCallId(sessionMessage.Content), message.ToolCallId, StringComparison.Ordinal));
            if (completed is not null)
            {
                message.ApplyCompletedTool(completed);
            }
        }
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    private async Task RunOnUiAsync(Func<Task> action)
    {
        if (_dispatcher.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }

        await _dispatcher.InvokeAsync(action).Task.Unwrap().ConfigureAwait(true);
    }

    private void RunOnUiSync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }
}

