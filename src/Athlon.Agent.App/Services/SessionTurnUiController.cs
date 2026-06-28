using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
public sealed class SessionTurnUiController
{
    private static readonly Action NoOpScroll = () => { };
    private const int MaxMessagesInMemory = 200;
    private const int TrimThreshold = 250;

    private readonly Dispatcher _dispatcher;
    private readonly SessionStreamingUiContext _streaming = new();
    private readonly SessionModifiedFilesTracker _modifiedFilesTracker = new();
    private readonly ToolCallArgsDisplayCoordinator _displayCoordinator = new();
    private readonly StreamingTokenBuffer _tokenBuffer;
    // Cache ViewModels by message ID so switching back to a previously-viewed
    // session reuses MarkdownMessageView / FlowDocument instead of rebuilding everything.
    private readonly Dictionary<string, ChatMessageViewModel> _viewModelCache = new(StringComparer.Ordinal);
    private int _bulkChatViewSyncDepth;
    private int _syncChatViewGeneration;
    private Func<bool> _showToolCalls = () => false;

    private Action _requestScroll = NoOpScroll;
    private Action _requestScrollImmediate = NoOpScroll;

    public SessionTurnUiController(Dispatcher dispatcher, Action? requestScroll = null, Action? requestScrollImmediate = null)
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

    /// <summary>WebChatView 实例（由 MainWindow 在初始化后注入），用于增量渲染消息。</summary>
    public WebChatView? ChatView { get; set; }

    public bool IsDisplayed { get; private set; }

    public void SetDisplayed(bool displayed)
    {
        if (IsDisplayed == displayed)
        {
            return;
        }

        IsDisplayed = displayed;
        RunOnUiSync(() =>
        {
            if (displayed)
            {
                FlushBufferedStreamingToUi();
            }
            else
            {
                _tokenBuffer.StopFlushTimer();
            }
        });
    }

    public Action<SessionUsageSnapshot>? OnUsageRecorded { get; set; }

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
        OnStreamEvent = streamEvent =>
        {
            if (streamEvent is AgentStreamEvent.UsageRecorded(var snapshot))
            {
                OnUsageRecorded?.Invoke(snapshot);
                return Task.CompletedTask;
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
                        RunOnUiSync(() => _modifiedFilesTracker.Process(streamEvent));
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

    public void AddUserMessage(string input, IReadOnlyList<ImageAttachment> imageAttachments)
    {
        RunOnUiSync(() =>
        {
            var vm = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, input, imageAttachments: imageAttachments));
            Messages.Add(vm);
            TrimMessagesIfNeeded();
            RequestScrollImmediate();
        });
    }

    public void ResetForTurn()
    {
        RunOnUiSync(() =>
        {
            _tokenBuffer.ClearBuffers();
            _tokenBuffer.StopFlushTimer();
            _streaming.Reset();
            _displayCoordinator.Reset();
        });
    }

    public void Release()
    {
        RunOnUiSync(() =>
        {
            ResetForTurn();
            _bulkChatViewSyncDepth++;
            try
            {
                Messages.Clear();
                _viewModelCache.Clear();
                _modifiedFilesTracker.Clear();
            }
            finally
            {
                _bulkChatViewSyncDepth--;
                SyncChatView(immediate: true);
            }
        });
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
            }
            else if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                _streaming.Process(new AgentStreamEvent.ClearEmptyAssistantPlaceholder(), Messages);
                foreach (var message in _streaming.ToolBubblesByIndex.Values.ToList())
                {
                    message.MarkStreamingToolCancelled();
                }
            }

            _streaming.Reset();
            ReconcilePendingToolsFromSession(session);
            _bulkChatViewSyncDepth++;
            try
            {
                ApplyPersistedTurnMessages(persistedTurnMessages, timedOut, turnTimeoutMinutes, errorMessage);
            }
            finally
            {
                _bulkChatViewSyncDepth--;
                FinalizeStreamingDisplay();
                RequestScrollImmediate();
            }
        });
    }

    public void HydrateFromSession(AgentSession session) =>
        RunOnUiSync(() => RebuildDisplayFromMessages(session.Messages));

    public void HydrateDisplay(AgentSession session, IReadOnlyList<ChatMessage> displayMessages) =>
        RunOnUiSync(() => RebuildDisplayFromMessages(displayMessages));

    public Task HydrateFromSessionAsync(AgentSession session) =>
        RunOnUiAsync(() => RebuildDisplayFromMessages(session.Messages));

    public Task HydrateDisplayAsync(AgentSession session, IReadOnlyList<ChatMessage> displayMessages) =>
        RunOnUiAsync(() => RebuildDisplayFromMessages(displayMessages));

    private void RebuildDisplayFromMessages(IReadOnlyList<ChatMessage> displayMessages)
    {
        _bulkChatViewSyncDepth++;
        Messages.Clear();
        _streaming.Reset();
        _displayCoordinator.Reset();

        // Prune cache: remove entries that belong to old sessions (not in the new display list)
        var currentIds = new HashSet<string>(displayMessages.Select(m => m.Id), StringComparer.Ordinal);
        var staleKeys = _viewModelCache.Keys.Where(k => !currentIds.Contains(k)).ToList();
        foreach (var key in staleKeys)
        {
            _viewModelCache.Remove(key);
        }

        const int batchSize = 50;
        var viewModels = ChatTimelineHydrator.BuildDisplayMessages(displayMessages, _viewModelCache, _showToolCalls());
        if (viewModels.Count <= batchSize)
        {
            foreach (var viewModel in viewModels)
            {
                Messages.Add(viewModel);
            }
        }
        else
        {
            // Batch-add to reduce UI layout passes for long conversations
            for (var i = 0; i < viewModels.Count; i += batchSize)
            {
                var batch = viewModels.Skip(i).Take(batchSize);
                foreach (var viewModel in batch)
                {
                    Messages.Add(viewModel);
                }
            }
        }

        // Cache ViewModels for future session switches
        foreach (var viewModel in viewModels)
        {
            _viewModelCache[viewModel.MessageId] = viewModel;
        }

        TrimMessagesIfNeeded();
        _modifiedFilesTracker.RebuildFromMessages(Messages);
        _bulkChatViewSyncDepth--;
        SyncChatView(immediate: true);
        RequestScrollImmediate();
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ChatView is null || _bulkChatViewSyncDepth > 0)
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
                    else if (!IsStreamingChatItem(single))
                    {
                        SyncChatView();
                    }
                }
                else
                {
                    SyncChatView();
                }

                break;
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                SyncChatView();
                break;
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

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                _ = ChatView.ApplyAssistantMarkdownAsync(message);
            }
        }
    }

    private void SyncChatView(bool immediate = false)
    {
        if (ChatView is null)
        {
            return;
        }

        if (immediate)
        {
            Interlocked.Increment(ref _syncChatViewGeneration);
            _ = ChatView.LoadMessagesAsync(Messages, _showToolCalls());
            return;
        }

        var generation = Interlocked.Increment(ref _syncChatViewGeneration);
        _dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation != _syncChatViewGeneration || ChatView is null)
            {
                return;
            }

            _ = ChatView.LoadMessagesAsync(Messages, _showToolCalls());
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
    }

    private bool ContainsMessageId(string messageId) =>
        !string.IsNullOrWhiteSpace(messageId)
        && Messages.Any(message => string.Equals(message.MessageId, messageId, StringComparison.Ordinal));

    private void AppendCompactionNotice(ChatMessage message) =>
        _streaming.Process(new AgentStreamEvent.ChatMessageAppended(message), Messages);

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

    private void ProcessUiStreamEvents(AgentStreamEvent streamEvent, bool notifyTracker)
    {
        if (notifyTracker)
        {
            _modifiedFilesTracker.Process(streamEvent);
        }

        foreach (var uiEvent in _displayCoordinator.MapForUi(streamEvent))
        {
            DispatchToChatView(uiEvent);
            _streaming.Process(uiEvent, Messages);
            NotifyChatViewAfterStreamEvent(uiEvent);
        }
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
            if (assistant is not null && !string.IsNullOrWhiteSpace(assistant.Content))
            {
                _ = ChatView.ApplyAssistantMarkdownAsync(assistant);
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

        if (pendingReasoning.Length > 0 && reasoningMessageId is not null)
        {
            _ = ChatView.DispatchEventAsync(new AgentStreamEvent.ReasoningMessageContent(reasoningMessageId, pendingReasoning));
        }

        if (pendingTokens.Length > 0 && textMessageId is not null)
        {
            _ = ChatView.DispatchEventAsync(new AgentStreamEvent.TextMessageContent(textMessageId, pendingTokens));
        }
    }

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

    private bool ShouldDispatchToChatView(AgentStreamEvent streamEvent) =>
        streamEvent is not AgentStreamEvent.UsageRecorded
            and not AgentStreamEvent.ContextHygieneApplied
            and not AgentStreamEvent.ChatMessageAppended
            and not AgentStreamEvent.ClearEmptyAssistantPlaceholder
        && (_showToolCalls() || !ChatDisplayPolicy.IsToolStreamEvent(streamEvent));

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
                AppendCompactionNotice(message);
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

