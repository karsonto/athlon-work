using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Threading;
using Athlon.Agent.App.Services.Streaming;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>Mutable session handle shared with turn callbacks so compaction sees the latest messages.</summary>
public sealed class LiveAgentSession
{
    public LiveAgentSession(AgentSession value) => Value = value;

    public AgentSession Value { get; set; }
}

/// <summary>Per-session chat UI state (messages + streaming buffers) for parallel turns.</summary>
public sealed class SessionTurnUiController
{
    private static readonly Action NoOpScroll = () => { };
    private static readonly TimeSpan StreamingFlushInterval = TimeSpan.FromMilliseconds(60);

    private readonly Dispatcher _dispatcher;
    private readonly SessionStreamingUiContext _streaming = new();
    private readonly object _bufferLock = new();
    private readonly object _scheduleLock = new();
    private bool _streamingFlushTimerActive;
    private readonly Queue<AgentStreamEvent> _pendingStreamEvents = new();
    private readonly StringBuilder _streamingTokenBuffer = new();
    private readonly StringBuilder _streamingReasoningBuffer = new();
    private string? _pendingTextMessageId;
    private string? _pendingReasoningMessageId;
    private DispatcherTimer? _streamingFlushTimer;

    private Action _requestScroll = NoOpScroll;
    private Action _requestScrollImmediate = NoOpScroll;

    public SessionTurnUiController(Dispatcher dispatcher, Action? requestScroll = null, Action? requestScrollImmediate = null)
    {
        _dispatcher = dispatcher;
        RequestScroll = requestScroll ?? NoOpScroll;
        RequestScrollImmediate = requestScrollImmediate ?? requestScroll ?? NoOpScroll;
        Messages = new ObservableCollection<ChatMessageViewModel>();
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; }

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
                StopStreamingFlushTimer();
            }
        });
    }

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
        OnStreamEvent = streamEvent =>
        {
            switch (streamEvent)
            {
                case AgentStreamEvent.TextMessageContent(var messageId, var delta):
                    lock (_bufferLock)
                    {
                        _streamingTokenBuffer.Append(delta);
                        _pendingTextMessageId = messageId;
                    }

                    if (IsDisplayed)
                    {
                        ScheduleStreamingFlush();
                    }

                    return Task.CompletedTask;
                case AgentStreamEvent.ReasoningMessageContent(var messageId, var delta):
                    lock (_bufferLock)
                    {
                        _streamingReasoningBuffer.Append(delta);
                        _pendingReasoningMessageId = messageId;
                    }

                    if (IsDisplayed)
                    {
                        ScheduleStreamingFlush();
                    }

                    return Task.CompletedTask;
                default:
                    if (!IsDisplayed)
                    {
                        lock (_bufferLock)
                        {
                            _pendingStreamEvents.Enqueue(streamEvent);
                        }

                        return Task.CompletedTask;
                    }

                    return RunOnUiAsync(() =>
                    {
                        FlushBufferedStreamingToUi();
                        _streaming.Process(streamEvent, Messages);
                    });
            }
        }
    };

    public void AddUserMessage(string input, IReadOnlyList<ImageAttachment> imageAttachments)
    {
        RunOnUiSync(() =>
        {
            Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, input, imageAttachments: imageAttachments)));
            RequestScrollImmediate();
        });
    }

    public void ResetForTurn()
    {
        RunOnUiSync(() =>
        {
            lock (_bufferLock)
            {
                _pendingStreamEvents.Clear();
                _streamingTokenBuffer.Clear();
                _streamingReasoningBuffer.Clear();
                _pendingTextMessageId = null;
                _pendingReasoningMessageId = null;
            }

            StopStreamingFlushTimer();
            _streaming.Reset();
        });
    }

    public void Release()
    {
        RunOnUiSync(() =>
        {
            ResetForTurn();
            Messages.Clear();
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

            string pendingTokens;
            string pendingReasoning;
            lock (_bufferLock)
            {
                pendingTokens = _streamingTokenBuffer.ToString();
                pendingReasoning = _streamingReasoningBuffer.ToString();
            }

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
            StopStreamingFlushTimer();
            FlushBufferedStreamingToUi();
            FlushStreamingTokens();
            lock (_bufferLock)
            {
                _streamingTokenBuffer.Clear();
                _streamingReasoningBuffer.Clear();
                _pendingStreamEvents.Clear();
                _pendingTextMessageId = null;
                _pendingReasoningMessageId = null;
            }

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
            ApplyPersistedTurnMessages(persistedTurnMessages, timedOut, turnTimeoutMinutes, errorMessage);
        });
    }

    public void HydrateFromSession(AgentSession session) =>
        RunOnUiSync(() => RebuildDisplayFromMessages(session.Messages));

    public void HydrateDisplay(AgentSession session, IReadOnlyList<ChatMessage> displayMessages) =>
        RunOnUiSync(() => RebuildDisplayFromMessages(displayMessages));

    private void RebuildDisplayFromMessages(IReadOnlyList<ChatMessage> displayMessages)
    {
        Messages.Clear();
        _streaming.Reset();
        var answeredToolCallIds = BuildAnsweredToolCallIds(displayMessages);

        foreach (var message in ChatTimelineOrder.OrderForDisplay(displayMessages))
        {
            AddMessageToDisplay(message, answeredToolCallIds);
        }

        RequestScrollImmediate();
    }

    private void AddMessageToDisplay(ChatMessage message, HashSet<string> answeredToolCallIds)
    {
        if (ShouldHideMessageFromChat(message) || ContainsMessageId(message.Id))
        {
            return;
        }

        Messages.Add(new ChatMessageViewModel(message));

        if (message.Role != MessageRole.Assistant)
        {
            return;
        }

        var pendingCalls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
        if (pendingCalls is null)
        {
            return;
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
            var orphanMessage = ChatMessage.Create(MessageRole.Tool, orphanResult, message.ParentId);
            if (ContainsMessageId(orphanMessage.Id))
            {
                continue;
            }

            Messages.Add(new ChatMessageViewModel(orphanMessage));
            answeredToolCallIds.Add(toolCall.Id);
        }
    }

    private bool ContainsMessageId(string messageId) =>
        !string.IsNullOrWhiteSpace(messageId)
        && Messages.Any(message => string.Equals(message.MessageId, messageId, StringComparison.Ordinal));

    private void AppendCompactionNotice(ChatMessage message) =>
        _streaming.Process(new AgentStreamEvent.ChatMessageAppended(message), Messages);

    private static bool ShouldHideMessageFromChat(ChatMessage message) =>
        message.Role == MessageRole.User && SummaryMessageBuilder.IsSummaryMessage(message)
        || ChatMessageViewModel.IsAssistantToolCallsOnly(message);

    private void FlushBufferedStreamingToUi()
    {
        Queue<AgentStreamEvent> pendingEvents;
        lock (_bufferLock)
        {
            pendingEvents = new Queue<AgentStreamEvent>(_pendingStreamEvents);
            _pendingStreamEvents.Clear();
        }

        while (pendingEvents.Count > 0)
        {
            _streaming.Process(pendingEvents.Dequeue(), Messages);
        }

        FlushStreamingTokens();
    }

    private void ScheduleStreamingFlush()
    {
        if (!IsDisplayed)
        {
            return;
        }

        lock (_scheduleLock)
        {
            if (_streamingFlushTimerActive)
            {
                return;
            }

            _streamingFlushTimerActive = true;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, StartStreamingFlushTimer);
    }

    private void StartStreamingFlushTimer()
    {
        if (_streamingFlushTimer is not null)
        {
            return;
        }

        _streamingFlushTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = StreamingFlushInterval
        };
        _streamingFlushTimer.Tick += (_, _) => FlushStreamingTokens();
        _streamingFlushTimer.Start();
    }

    private void FlushStreamingTokens()
    {
        string pendingTokens;
        string pendingReasoning;
        lock (_bufferLock)
        {
            if (_streamingTokenBuffer.Length == 0 && _streamingReasoningBuffer.Length == 0)
            {
                return;
            }

            pendingTokens = _streamingTokenBuffer.ToString();
            pendingReasoning = _streamingReasoningBuffer.ToString();
            if (pendingTokens.Length > 0)
            {
                _streamingTokenBuffer.Clear();
            }

            if (pendingReasoning.Length > 0)
            {
                _streamingReasoningBuffer.Clear();
            }
        }

        string? textMessageId;
        string? reasoningMessageId;
        lock (_bufferLock)
        {
            textMessageId = _pendingTextMessageId;
            reasoningMessageId = _pendingReasoningMessageId;
        }

        var didFlush = false;
        if (pendingTokens.Length > 0 && textMessageId is not null)
        {
            _streaming.Process(new AgentStreamEvent.TextMessageContent(textMessageId, pendingTokens), Messages);
            didFlush = true;
        }

        if (pendingReasoning.Length > 0 && reasoningMessageId is not null)
        {
            _streaming.Process(new AgentStreamEvent.ReasoningMessageContent(reasoningMessageId, pendingReasoning), Messages);
            didFlush = true;
        }

        if (didFlush && IsDisplayed)
        {
            RequestScroll();
        }
    }

    private void StopStreamingFlushTimer()
    {
        if (_streamingFlushTimer is null)
        {
            lock (_scheduleLock)
            {
                _streamingFlushTimerActive = false;
            }

            return;
        }

        _streamingFlushTimer.Stop();
        _streamingFlushTimer = null;
        lock (_scheduleLock)
        {
            _streamingFlushTimerActive = false;
        }
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
        var answered = BuildAnsweredToolCallIds(session.Messages);
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
                var toolCallId = ExtractToolCallId(message.Content);
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
