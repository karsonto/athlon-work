using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Threading;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

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
    private static readonly TimeSpan ToolFlushInterval = TimeSpan.FromMilliseconds(60);

    private readonly Dispatcher _dispatcher;
    private readonly object _bufferLock = new();
    private readonly object _scheduleLock = new();
    private bool _streamingFlushTimerActive;
    private bool _streamingToolFlushTimerActive;
    private readonly Dictionary<int, ChatMessageViewModel> _streamingToolMessagesByIndex = new();
    private readonly Dictionary<int, StreamingToolCallDelta> _pendingToolCallDeltas = new();
    private readonly StringBuilder _streamingTokenBuffer = new();
    private readonly StringBuilder _streamingReasoningBuffer = new();
    private ChatMessageViewModel? _streamingAssistantMessage;
    private DispatcherTimer? _streamingFlushTimer;
    private DispatcherTimer? _streamingToolFlushTimer;

    public SessionTurnUiController(Dispatcher dispatcher, Action? requestScroll = null)
    {
        _dispatcher = dispatcher;
        RequestScroll = requestScroll ?? NoOpScroll;
        RequestScrollImmediate = requestScroll ?? NoOpScroll;
        Messages = new ObservableCollection<ChatMessageViewModel>();
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; }

    public Action RequestScroll { get; set; }

    public Action RequestScrollImmediate { get; set; }

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
                StopStreamingToolFlushTimer();
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
        OnToolStarted = async toolCall => await RunOnUiAsync(() => HandleToolStarted(toolCall)),
        OnAssistantToolCallDelta = delta =>
        {
            lock (_bufferLock)
            {
                _pendingToolCallDeltas[delta.Index] = delta;
            }

            if (IsDisplayed)
            {
                ScheduleStreamingToolFlush();
            }

            return Task.CompletedTask;
        },
        OnMessage = async message => await RunOnUiAsync(() => HandleMessage(message, liveSession?.Value)),
        OnAssistantTextDelta = token =>
        {
            lock (_bufferLock)
            {
                _streamingTokenBuffer.Append(token);
            }

            if (IsDisplayed)
            {
                ScheduleStreamingFlush();
            }

            return Task.CompletedTask;
        },
        OnAssistantReasoningDelta = token =>
        {
            lock (_bufferLock)
            {
                _streamingReasoningBuffer.Append(token);
            }

            if (IsDisplayed)
            {
                ScheduleStreamingFlush();
            }

            return Task.CompletedTask;
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
                _pendingToolCallDeltas.Clear();
                _streamingTokenBuffer.Clear();
                _streamingReasoningBuffer.Clear();
            }

            StopStreamingFlushTimer();
            StopStreamingToolFlushTimer();
            _streamingAssistantMessage = null;
            _streamingToolMessagesByIndex.Clear();
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
            FlushStreamingToolDeltas();

            string pendingTokens;
            string pendingReasoning;
            lock (_bufferLock)
            {
                pendingTokens = _streamingTokenBuffer.ToString();
                pendingReasoning = _streamingReasoningBuffer.ToString();
            }

            var assistantContent = _streamingAssistantMessage?.Content;
            if (pendingTokens.Length > 0)
            {
                assistantContent = (assistantContent ?? string.Empty) + pendingTokens;
            }

            var assistantReasoning = _streamingAssistantMessage?.ReasoningContent;
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
            StopStreamingToolFlushTimer();
            FlushBufferedStreamingToUi();
            FlushStreamingTokens();
            FlushStreamingToolDeltas();
            lock (_bufferLock)
            {
                _streamingTokenBuffer.Clear();
                _streamingReasoningBuffer.Clear();
                _pendingToolCallDeltas.Clear();
            }

            if (cancelled)
            {
                if (_streamingAssistantMessage is not null)
                {
                    _streamingAssistantMessage.MarkStreamingCancelled();
                    _streamingAssistantMessage = null;
                }

                foreach (var message in Messages.Where(static message => message.IsToolRunning))
                {
                    message.MarkToolCancelled();
                }

                foreach (var message in _streamingToolMessagesByIndex.Values.ToList())
                {
                    message.MarkStreamingToolCancelled();
                }
            }
            else if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                ClearStreamingAssistantPlaceholder();
                foreach (var message in _streamingToolMessagesByIndex.Values.ToList())
                {
                    message.MarkStreamingToolCancelled();
                }
            }

            _streamingAssistantMessage = null;
            _streamingToolMessagesByIndex.Clear();
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

    private void AppendCompactionNotice(ChatMessage message)
    {
        ClearStreamingAssistantPlaceholder();
        if (ContainsMessageId(message.Id))
        {
            return;
        }

        Messages.Add(new ChatMessageViewModel(message));
        RequestScroll();
    }

    private static bool ShouldHideMessageFromChat(ChatMessage message) =>
        message.Role == MessageRole.User && SummaryMessageBuilder.IsSummaryMessage(message)
        || ChatMessageViewModel.IsAssistantToolCallsOnly(message);

    private void HandleToolStarted(AgentToolCall toolCall)
    {
        FlushStreamingTokens();
        FlushStreamingToolDeltas();
        ClearStreamingAssistantPlaceholder();
        var existing = FindToolMessage(toolCall.Id);
        if (existing is not null)
        {
            if (existing.ToolCallStatus == ToolCallDisplayStatus.Preparing)
            {
                if (existing.StreamToolIndex is int streamIndex)
                {
                    lock (_bufferLock)
                    {
                        _pendingToolCallDeltas.Remove(streamIndex);
                    }
                }

                existing.PromoteStreamingToolToRunning(toolCall);
                RemoveStreamingToolTracking(existing);
            }

            return;
        }

        var preparing = _streamingToolMessagesByIndex.Values.FirstOrDefault(message =>
            message.ToolCallStatus == ToolCallDisplayStatus.Preparing
            && (string.IsNullOrWhiteSpace(message.ToolCallId)
                || string.Equals(message.ToolCallId, toolCall.Id, StringComparison.Ordinal)));
        if (preparing is not null)
        {
            if (preparing.StreamToolIndex is int streamIndex)
            {
                lock (_bufferLock)
                {
                    _pendingToolCallDeltas.Remove(streamIndex);
                }
            }

            preparing.PromoteStreamingToolToRunning(toolCall);
            RemoveStreamingToolTracking(preparing);
            RequestScroll();
            return;
        }

        Messages.Add(ChatMessageViewModel.CreatePendingTool(toolCall));
        RequestScroll();
    }

    private void HandleMessage(ChatMessage message, AgentSession? _)
    {
        if (ShouldHideMessageFromChat(message))
        {
            if (message.Role == MessageRole.User && SummaryMessageBuilder.IsSummaryMessage(message))
            {
                ClearStreamingAssistantPlaceholder();
            }

            return;
        }

        if (message.Role == MessageRole.Compaction)
        {
            AppendCompactionNotice(message);
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

            if (!ContainsMessageId(message.Id))
            {
                Messages.Add(new ChatMessageViewModel(message));
                RequestScroll();
            }

            return;
        }

        if (message.Role == MessageRole.Assistant && _streamingAssistantMessage is not null)
        {
            FlushStreamingTokens();
            _streamingAssistantMessage.CompleteStreamingAssistant(message);
            _streamingAssistantMessage = null;
            RequestScrollImmediate();
            return;
        }

        ClearStreamingAssistantPlaceholder();
        if (!ContainsMessageId(message.Id))
        {
            Messages.Add(new ChatMessageViewModel(message));
            RequestScrollImmediate();
        }
    }

    private void EnsureStreamingAssistantMessage()
    {
        if (_streamingAssistantMessage is not null)
        {
            return;
        }

        _streamingAssistantMessage = ChatMessageViewModel.CreateStreamingAssistant();
        Messages.Add(_streamingAssistantMessage);
        RequestScroll();
    }

    private void ClearStreamingAssistantPlaceholder()
    {
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

    private void FlushBufferedStreamingToUi()
    {
        bool hasTokenBuffer;
        bool hasToolDeltas;
        lock (_bufferLock)
        {
            hasTokenBuffer = _streamingTokenBuffer.Length > 0 || _streamingReasoningBuffer.Length > 0;
            hasToolDeltas = _pendingToolCallDeltas.Count > 0;
        }

        if (!hasTokenBuffer && !hasToolDeltas)
        {
            return;
        }

        if (hasTokenBuffer)
        {
            EnsureStreamingAssistantMessage();
        }

        if (hasToolDeltas)
        {
            ClearStreamingAssistantPlaceholder();
        }

        FlushStreamingTokens();
        FlushStreamingToolDeltas();
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
        bool hasContent;
        lock (_bufferLock)
        {
            hasContent = _streamingTokenBuffer.Length > 0 || _streamingReasoningBuffer.Length > 0;
        }

        if (!hasContent)
        {
            return;
        }

        EnsureStreamingAssistantMessage();

        string pendingTokens;
        string pendingReasoning;
        lock (_bufferLock)
        {
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

        var didFlush = false;
        if (pendingTokens.Length > 0)
        {
            _streamingAssistantMessage.AppendStreamingToken(pendingTokens);
            didFlush = true;
        }

        if (pendingReasoning.Length > 0)
        {
            _streamingAssistantMessage.AppendStreamingReasoningToken(pendingReasoning);
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

    private void ScheduleStreamingToolFlush()
    {
        if (!IsDisplayed)
        {
            return;
        }

        lock (_scheduleLock)
        {
            if (_streamingToolFlushTimerActive)
            {
                return;
            }

            _streamingToolFlushTimerActive = true;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, StartStreamingToolFlushTimer);
    }

    private void StartStreamingToolFlushTimer()
    {
        if (_streamingToolFlushTimer is not null)
        {
            return;
        }

        _streamingToolFlushTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = ToolFlushInterval
        };
        _streamingToolFlushTimer.Tick += (_, _) =>
        {
            ClearStreamingAssistantPlaceholder();
            FlushStreamingToolDeltas();
        };
        _streamingToolFlushTimer.Start();
    }

    private void FlushStreamingToolDeltas()
    {
        List<KeyValuePair<int, StreamingToolCallDelta>> deltas;
        lock (_bufferLock)
        {
            if (_pendingToolCallDeltas.Count == 0)
            {
                return;
            }

            deltas = _pendingToolCallDeltas.ToList();
        }

        var didFlush = false;
        foreach (var (index, delta) in deltas)
        {
            if (!_streamingToolMessagesByIndex.TryGetValue(index, out var toolMessage))
            {
                toolMessage = ChatMessageViewModel.CreateStreamingTool(index);
                _streamingToolMessagesByIndex[index] = toolMessage;
                Messages.Add(toolMessage);
            }

            toolMessage.UpdateStreamingToolCall(delta.Id, delta.Name, delta.ArgumentsJson);
            didFlush = true;
        }

        if (didFlush && IsDisplayed)
        {
            RequestScroll();
        }
    }

    private void StopStreamingToolFlushTimer()
    {
        if (_streamingToolFlushTimer is null)
        {
            lock (_scheduleLock)
            {
                _streamingToolFlushTimerActive = false;
            }

            return;
        }

        _streamingToolFlushTimer.Stop();
        _streamingToolFlushTimer = null;
        lock (_scheduleLock)
        {
            _streamingToolFlushTimerActive = false;
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

    private void RemoveStreamingToolTracking(ChatMessageViewModel message)
    {
        if (message.StreamToolIndex is int index)
        {
            _streamingToolMessagesByIndex.Remove(index);
            return;
        }

        foreach (var entry in _streamingToolMessagesByIndex.ToList())
        {
            if (ReferenceEquals(entry.Value, message))
            {
                _streamingToolMessagesByIndex.Remove(entry.Key);
                break;
            }
        }
    }

    private IReadOnlyList<AgentToolCall> CollectIncompleteToolCalls(AgentSession session)
    {
        var answered = BuildAnsweredToolCallIds(session.Messages);
        var incomplete = new Dictionary<string, AgentToolCall>(StringComparer.Ordinal);

        List<StreamingToolCallDelta> pendingDeltas;
        lock (_bufferLock)
        {
            pendingDeltas = _pendingToolCallDeltas.Values.OrderBy(item => item.Index).ToList();
        }

        foreach (var delta in pendingDeltas)
        {
            if (string.IsNullOrWhiteSpace(delta.Id) || answered.Contains(delta.Id))
            {
                continue;
            }

            incomplete[delta.Id] = new AgentToolCall(
                delta.Id,
                delta.Name ?? string.Empty,
                ToolCallArgumentsParser.ParseJson(delta.ArgumentsJson));
        }

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
                if (_streamingAssistantMessage is not null)
                {
                    _streamingAssistantMessage.CompleteStreamingAssistant(message);
                    _streamingAssistantMessage = null;
                    RequestScrollImmediate();
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
