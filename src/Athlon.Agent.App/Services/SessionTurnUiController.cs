using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Threading;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.App.Services;

/// <summary>Per-session chat UI state (messages + streaming buffers) for parallel turns.</summary>
public sealed class SessionTurnUiController
{
    private static readonly Action NoOpScroll = () => { };
    private static readonly TimeSpan StreamingFlushInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan ToolFlushInterval = TimeSpan.FromMilliseconds(16);

    private readonly Dispatcher _dispatcher;
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
        Messages = new ObservableCollection<ChatMessageViewModel>();
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; }

    public Action RequestScroll { get; set; }

    public AgentTurnCallbacks BuildCallbacks() => new()
    {
        OnToolStarted = async toolCall => await RunOnUiAsync(() => HandleToolStarted(toolCall)),
        OnAssistantToolCallDelta = delta =>
        {
            _pendingToolCallDeltas[delta.Index] = delta;
            return RunOnUiAsync(() =>
            {
                ClearStreamingAssistantPlaceholder();
                ScheduleStreamingToolFlush();
            });
        },
        OnMessage = async message => await RunOnUiAsync(() => HandleMessage(message)),
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

    public void AddUserMessage(string input, IReadOnlyList<ImageAttachment> imageAttachments)
    {
        RunOnUiSync(() =>
        {
            Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, input, imageAttachments: imageAttachments)));
            RequestScroll();
        });
    }

    public void ResetForTurn()
    {
        RunOnUiSync(() =>
        {
            _pendingToolCallDeltas.Clear();
            StopStreamingFlushTimer();
            StopStreamingToolFlushTimer();
            _streamingTokenBuffer.Clear();
            _streamingReasoningBuffer.Clear();
            _streamingAssistantMessage = null;
            _streamingToolMessagesByIndex.Clear();
        });
    }

    public void FinalizeTurn(
        AgentSession session,
        bool cancelled,
        bool timedOut,
        int turnTimeoutMinutes,
        string? errorMessage = null)
    {
        RunOnUiSync(() =>
        {
            StopStreamingFlushTimer();
            StopStreamingToolFlushTimer();
            FlushStreamingTokens();
            FlushStreamingToolDeltas();
            _streamingTokenBuffer.Clear();
            _streamingReasoningBuffer.Clear();
            _pendingToolCallDeltas.Clear();

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

                var notice = timedOut
                    ? $"本回合已超过 {turnTimeoutMinutes} 分钟，已自动停止。"
                    : "生成已停止。";
                Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.System, notice)));
            }
            else if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                ClearStreamingAssistantPlaceholder();
                foreach (var message in _streamingToolMessagesByIndex.Values.ToList())
                {
                    message.MarkStreamingToolCancelled();
                }

                Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.System, errorMessage)));
            }

            _streamingAssistantMessage = null;
            _streamingToolMessagesByIndex.Clear();
            ReconcilePendingToolsFromSession(session);
        });
    }

    public void HydrateFromSession(AgentSession session)
    {
        RunOnUiSync(() =>
        {
            Messages.Clear();
            var answeredToolCallIds = BuildAnsweredToolCallIds(session.Messages);

            foreach (var message in session.Messages)
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
        });
    }

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
                    _pendingToolCallDeltas.Remove(streamIndex);
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
                _pendingToolCallDeltas.Remove(streamIndex);
            }

            preparing.PromoteStreamingToolToRunning(toolCall);
            RemoveStreamingToolTracking(preparing);
            RequestScroll();
            return;
        }

        Messages.Add(ChatMessageViewModel.CreatePendingTool(toolCall));
        RequestScroll();
    }

    private void HandleMessage(ChatMessage message)
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
            RequestScroll();
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
            RequestScroll();
            return;
        }

        if (message.Role == MessageRole.Assistant && _streamingAssistantMessage is not null)
        {
            FlushStreamingTokens();
            _streamingAssistantMessage.CompleteStreamingAssistant(message);
            _streamingAssistantMessage = null;
            RequestScroll();
            return;
        }

        ClearStreamingAssistantPlaceholder();
        Messages.Add(new ChatMessageViewModel(message));
        RequestScroll();
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

    private void ScheduleStreamingFlush()
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
            RequestScroll();
        }
    }

    private void StopStreamingFlushTimer()
    {
        if (_streamingFlushTimer is null)
        {
            return;
        }

        _streamingFlushTimer.Stop();
        _streamingFlushTimer = null;
    }

    private void ScheduleStreamingToolFlush()
    {
        if (_streamingToolFlushTimer is not null)
        {
            return;
        }

        _streamingToolFlushTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
        {
            Interval = ToolFlushInterval
        };
        _streamingToolFlushTimer.Tick += (_, _) => FlushStreamingToolDeltas();
        _streamingToolFlushTimer.Start();
    }

    private void FlushStreamingToolDeltas()
    {
        if (_pendingToolCallDeltas.Count == 0)
        {
            return;
        }

        var didFlush = false;
        foreach (var (index, delta) in _pendingToolCallDeltas.ToList())
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

        if (didFlush)
        {
            RequestScroll();
        }
    }

    private void StopStreamingToolFlushTimer()
    {
        if (_streamingToolFlushTimer is null)
        {
            return;
        }

        _streamingToolFlushTimer.Stop();
        _streamingToolFlushTimer = null;
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
