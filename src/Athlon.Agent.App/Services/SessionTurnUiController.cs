using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Services.Diagnostics;
using Athlon.Agent.App.Services.Streaming;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Plan;
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
    private const int MaxViewModelCacheSize = 1000;

    private readonly Dispatcher _dispatcher;
    private readonly SessionStreamingUiContext _streaming = new();
    private readonly SessionModifiedFilesTracker _modifiedFilesTracker = new();
    private readonly SessionTurnActivityTracker _turnActivityTracker = new();
    /// <summary>
    /// Full transcript (including folded activity tools) used to replay TURN_ACTIVITY / FILES_CHANGED.
    /// <see cref="Messages"/> omits those tools when show-tool-calls is off.
    /// </summary>
    private List<ChatMessage> _activitySourceMessages = new();
    private List<ChatMessage> _displayMessages = new();
    private ConversationDisplayCursor? _olderDisplayCursor;
    /// <summary>
    /// Manual-compaction audits whose display collapse has already been applied. Model-driven
    /// compaction never collapses the timeline, so it must not be listed here.
    /// </summary>
    private readonly HashSet<string> _appliedManualCompactionAuditIds = new(StringComparer.Ordinal);
    private readonly object _manualCompactionRefreshGate = new();
    private readonly ToolCallArgsDisplayCoordinator _displayCoordinator = new();
    private readonly StreamingTokenBuffer _tokenBuffer;
    private readonly ConcurrentDictionary<string, PendingUiApproval> _pendingApprovals =
        new(StringComparer.Ordinal);
    // Cache ViewModels by message ID so switching back to a previously-viewed
    // session reuses MarkdownMessageView / FlowDocument instead of rebuilding everything.
    private readonly Dictionary<string, ChatMessageViewModel> _viewModelCache = new(StringComparer.Ordinal);
    private int _bulkChatViewSyncDepth;
    private int _syncChatViewGeneration;
    /// <summary>
    /// Message id of the user message that opened the turn currently being streamed. Used as the
    /// activity/files entry key so a live card and the replayed card for the same turn collide
    /// (and merge) instead of appearing twice.
    /// </summary>
    private string? _currentTurnAnchorId;
    /// <summary>
    /// The plan run the timeline is currently showing a plan-ready card for. Kept here because the
    /// card is the one timeline entry the transcript cannot rebuild: <c>publish_plan</c> renders as
    /// a plan card, not a tool card, so a full replay would otherwise drop it. The controller
    /// re-emits it onto its <see cref="TimelineOrderPolicy.Plan"/> slot after every reload.
    /// </summary>
    private PlanRun? _visiblePlanRun;
    private Func<bool> _showToolCalls = () => true;
    /// <summary>
    /// Ordinal of the activity fold being accumulated inside the current turn. The fold's entry id
    /// is <c>activity:&lt;anchor&gt;:&lt;index&gt;</c>, and the index increments each time the fold is
    /// sealed by an assistant bubble so the live publish and the replay agree on the same key.
    /// </summary>
    private int _activityBlockIndex;

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

    /// <summary>
    /// True while the current turn has touched a file. Part of the live-turn gate: a full replay
    /// while it holds would re-emit cards the live per-edit publish already rendered.
    /// </summary>
    internal bool HasLiveFileEdits => _modifiedFilesTracker.HasCurrentTurnPaths;

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

    /// <summary>
    /// Session this controller renders. Assigned by <see cref="SessionUiCache"/> so cache-backed
    /// paths (replay shard cache) can key by the conversation the transcript belongs to.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Whether the chat scroll position is preserved (and restored) when this session is
    /// re-rendered. False restores the previous scroll-to-bottom behaviour. Bound to
    /// <c>Ui.PreserveSessionUiState</c>.
    /// </summary>
    public bool PreserveSessionScroll { get; set; } = true;

    /// <summary>
    /// Replay shard cache shared across sessions. Assigned by <see cref="SessionUiCache"/>; null in
    /// unit tests, where reloads use <see cref="ReloadChatViewOverride"/> instead.
    /// </summary>
    public ChatReplaySnapshotCache? ReplayCache { get; set; }

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

    internal IReadOnlyList<ChatMessage> DisplayMessagesSnapshot => _displayMessages;

    internal ConversationDisplayCursor? OlderDisplayCursor => _olderDisplayCursor;

    /// <summary>Older-messages cursor for runtime hydration after a turn.</summary>
    internal ConversationDisplayCursor? CaptureOlderDisplayCursor()
    {
        ConversationDisplayCursor? olderDisplayCursor = null;
        RunOnUiSync(() => olderDisplayCursor = _olderDisplayCursor);
        return olderDisplayCursor;
    }

    /// <summary>Test seam: activity source sliced to the displayed window for WebView replay.</summary>
    internal IReadOnlyList<ChatMessage> ReplayActivitySource => BuildReplayActivitySource();

    /// <summary>Test seam: the plan run whose card is currently kept across replays.</summary>
    internal PlanRun? VisiblePlanRun => _visiblePlanRun;

    /// <summary>Test seam: observes the plan-card events this controller decides to publish.</summary>
    internal Action<string>? PlanTimelineEventObserver { get; set; }

    /// <summary>Test seam: generation bumped when a chat-view sync is scheduled.</summary>
    internal int SyncChatViewGeneration => Volatile.Read(ref _syncChatViewGeneration);

    /// <summary>
    /// Test seam: anchor the live activity/files cards are published under. It must always be the
    /// transcript's own user message id, never the provisional id the UI minted in
    /// <see cref="AddUserMessage"/>, or a replay and a live upsert render two cards.
    /// </summary>
    internal string? CurrentTurnAnchorId => _currentTurnAnchorId;

    /// <summary>
    /// Materializes buffered tokens and returns the in-flight assistant row that is not yet
    /// durable on disk so a session switch can checkpoint it before flush.
    /// Tool messages are sync-flushed at message boundaries and do not need checkpointing.
    /// </summary>
    public IReadOnlyList<ChatMessage> CaptureStreamingCheckpoint()
    {
        IReadOnlyList<ChatMessage> checkpoint = Array.Empty<ChatMessage>();
        RunOnUiSync(() =>
        {
            FlushBufferedStreamingToUi();
            FlushStreamingTokens();

            var (pendingTokens, pendingReasoning, textMessageId, _) = _tokenBuffer.PeekPending();
            var messages = new List<ChatMessage>();

            var assistant = _streaming.ActiveAssistantBubble;
            var assistantId = assistant?.MessageId ?? textMessageId;
            var assistantContent = assistant?.Content ?? string.Empty;
            if (pendingTokens.Length > 0)
            {
                assistantContent += pendingTokens;
            }

            var assistantReasoning = assistant?.ReasoningContent ?? string.Empty;
            if (pendingReasoning.Length > 0)
            {
                assistantReasoning += pendingReasoning;
            }

            if (!string.IsNullOrWhiteSpace(assistantId)
                && (!string.IsNullOrWhiteSpace(assistantContent)
                    || !string.IsNullOrWhiteSpace(assistantReasoning)))
            {
                messages.Add(ChatMessage.CreateWithId(
                    assistantId,
                    MessageRole.Assistant,
                    assistantContent,
                    reasoningContent: string.IsNullOrWhiteSpace(assistantReasoning) ? null : assistantReasoning));
            }

            checkpoint = messages;
        });

        return checkpoint;
    }

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
                // Materialize tokens already received while this session was visible before
                // stopping the timer. Otherwise an immediate session switch can leave the
                // latest assistant text only in the transient buffer instead of this
                // session's UI cache.
                FlushBufferedStreamingToUi();
                _tokenBuffer.StopFlushTimer();
                // The shared WebChatView is about to show another session. A stale plan run would
                // otherwise be replayed into that session's timeline on its first render.
                _visiblePlanRun = null;
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

            if (ShouldRefreshDisplayAfterSessionReplace(session))
            {
                // Compaction replaces/removes transcript messages. Rebuild the
                // per-session cache even while hidden so it cannot become an
                // authoritative but stale surface when the user switches back.
                return RebuildDisplayFromMessagesAsync(
                    session.Messages,
                    synthesizeInterruptedToolResults: true);
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

    public void UpdateSurfaceCursor(ConversationDisplayCursor? olderDisplayCursor) =>
        RunOnUiSync(() => _olderDisplayCursor = olderDisplayCursor);

    public Task PrependDisplayMessagesAsync(
        IReadOnlyList<ChatMessage> olderDisplayMessages,
        ConversationDisplayCursor? olderDisplayCursor,
        bool showToolCalls,
        bool hasOlderMessages) =>
        RunOnUiTaskAsync(async () =>
        {
            if (olderDisplayMessages.Count == 0)
            {
                _olderDisplayCursor = olderDisplayCursor;
                if (IsDisplayed && ChatView is not null)
                {
                    await ChatView.SetOlderMessagesAvailableAsync(hasOlderMessages).ConfigureAwait(true);
                }

                return;
            }

            var prependViewModels = ChatTimelineHydrator.BuildDisplayMessages(
                olderDisplayMessages,
                viewModelCache: null,
                showToolCalls,
                synthesizeInterruptedToolResults: false);
            var visibleIds = new HashSet<string>(
                Messages.Where(message => !message.IsHiddenPlaceholder).Select(message => message.MessageId),
                StringComparer.Ordinal);
            var toInsert = prependViewModels
                .Where(viewModel => !visibleIds.Contains(viewModel.MessageId))
                .ToList();
            if (toInsert.Count > 0)
            {
                _bulkChatViewSyncDepth++;
                try
                {
                    var insertIndex = Messages.Count > 0 && Messages[0].IsHiddenPlaceholder ? 1 : 0;
                    foreach (var viewModel in toInsert)
                    {
                        Messages.Insert(insertIndex++, viewModel);
                        _viewModelCache[viewModel.MessageId] = viewModel;
                    }
                }
                finally
                {
                    _bulkChatViewSyncDepth--;
                }
            }

            _displayMessages = PrependDistinct(olderDisplayMessages, _displayMessages);
            _activitySourceMessages = PrependDistinct(olderDisplayMessages, _activitySourceMessages);
            _olderDisplayCursor = olderDisplayCursor;
            TrimMessagesIfNeeded();

            if (IsDisplayed && ChatView is not null)
            {
                await ChatView.PrependMessagesAsync(toInsert, showToolCalls, hasOlderMessages).ConfigureAwait(true);
            }
        });


    /// <summary>
    /// Reloads the whole chat timeline. <paramref name="authoritative"/> marks renders whose
    /// content cannot be produced incrementally (session switch, first paint, page prepend):
    /// those are always allowed. Refresh-style reloads are suppressed while a live turn still owns
    /// live FILES_CHANGED / TURN_ACTIVITY cards, so a mid-turn replay cannot stack a twin card
    /// next to the live one. The turn-end authoritative replay renders the canonical surface, so a
    /// suppressed refresh does not need to be queued.
    /// </summary>
    public async Task ReloadChatViewAsync(bool authoritative = false)
    {
        if (!IsDisplayed)
        {
            return;
        }

        // Evaluated before the renderer (including the host/test override): a refresh must not
        // re-render the timeline while a live turn still owns the FILES_CHANGED card.
        // The refresh is dropped rather than deferred: the turn-end authoritative replay below
        // renders the canonical surface anyway, so a deferred render would only duplicate it.
        if (!authoritative && HasLiveTurnSurface)
        {
            return;
        }

        if (ReloadChatViewOverride is not null)
        {
            await ReloadChatViewOverride().ConfigureAwait(true);
            return;
        }

        if (ChatView is null || !IsDisplayed)
        {
            return;
        }

        var chatView = ChatView;
        var activitySource = BuildReplayActivitySource();
        if (!IsDisplayed || !ReferenceEquals(ChatView, chatView))
        {
            return;
        }

        await chatView.LoadMessagesAsync(
                Messages,
                _showToolCalls(),
                activitySource.Count > 0 ? activitySource : null,
                _visiblePlanRun,
                PreserveSessionScroll ? SessionId : null,
                ReplayCache)
            .ConfigureAwait(true);
        if (ReferenceEquals(ChatView, chatView) && IsDisplayed)
        {
            // A full replay resets the JS timeline, which clears hasOlderMessages. Re-arm it so
            // scrolling to the top still loads older history without a session switch.
            await chatView.SetOlderMessagesAvailableAsync(_olderDisplayCursor is not null)
                .ConfigureAwait(true);
            await RestorePendingToolApprovalsAsync().ConfigureAwait(true);
            // Replay re-emits the current turn's activity/files entries under the same entry ids
            // the live upsert uses, so this re-publish overwrites them in place instead of stacking
            // a second card.
            RestoreLiveTurnCardsAfterReload();
        }
    }

    public void AddUserMessage(string input, IReadOnlyList<ImageAttachment> imageAttachments)
    {
        RunOnUiSync(() =>
        {
            _modifiedFilesTracker.BeginTurn();
            _turnActivityTracker.BeginTurn();
            _activityBlockIndex = 0;
            var message = ChatMessage.Create(MessageRole.User, input, imageAttachments: imageAttachments);
            AppendActivitySourceMessage(message);
            Messages.Add(new ChatMessageViewModel(message));
            // Anchor this turn's live activity/files cards to the user message that opened it.
            // Replay derives the same anchor from the transcript, so a live card and its replayed
            // twin share one key and overwrite each other instead of stacking.
            _currentTurnAnchorId = message.Id;
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
            _activityBlockIndex = 0;
            _tokenBuffer.ClearBuffers();
            _tokenBuffer.StopFlushTimer();
            _streaming.Reset();
            _displayCoordinator.Reset();
        });
    }

    public void Release()
    {
        // The page may still hold this session's rendered DOM snapshot; drop it because the
        // controller (and its revision) is going away with it.
        if (SessionId is { Length: > 0 } releasedSessionId)
        {
            ChatView?.InvalidateSessionSnapshotInPage(releasedSessionId);
        }

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
                _displayMessages = new List<ChatMessage>();
                _activitySourceMessages = new List<ChatMessage>();
                _olderDisplayCursor = null;
                _appliedManualCompactionAuditIds.Clear();
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

