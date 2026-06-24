using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Threading;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services.Streaming;

/// <summary>Batches streaming text/reasoning tokens and non-content stream events before UI flush.</summary>
internal sealed class StreamingTokenBuffer
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly Dispatcher _dispatcher;
    private readonly SessionStreamingUiContext _streaming;
    private readonly object _bufferLock = new();
    private readonly object _scheduleLock = new();
    private readonly Queue<AgentStreamEvent> _pendingStreamEvents = new();
    private readonly StringBuilder _streamingTokenBuffer = new();
    private readonly StringBuilder _streamingReasoningBuffer = new();
    private string? _pendingTextMessageId;
    private string? _pendingReasoningMessageId;
    private bool _streamingFlushTimerActive;
    private DispatcherTimer? _streamingFlushTimer;

    public StreamingTokenBuffer(Dispatcher dispatcher, SessionStreamingUiContext streaming)
    {
        _dispatcher = dispatcher;
        _streaming = streaming;
    }

    public void AppendTextToken(string messageId, string delta)
    {
        lock (_bufferLock)
        {
            _streamingTokenBuffer.Append(delta);
            _pendingTextMessageId = messageId;
        }
    }

    public void AppendReasoningToken(string messageId, string delta)
    {
        lock (_bufferLock)
        {
            _streamingReasoningBuffer.Append(delta);
            _pendingReasoningMessageId = messageId;
        }
    }

    public void EnqueueEvent(AgentStreamEvent streamEvent)
    {
        lock (_bufferLock)
        {
            _pendingStreamEvents.Enqueue(streamEvent);
        }
    }

    public void ClearBuffers()
    {
        lock (_bufferLock)
        {
            _pendingStreamEvents.Clear();
            _streamingTokenBuffer.Clear();
            _streamingReasoningBuffer.Clear();
            _pendingTextMessageId = null;
            _pendingReasoningMessageId = null;
        }
    }

    public (string Tokens, string Reasoning, string? TextMessageId, string? ReasoningMessageId) PeekPending()
    {
        lock (_bufferLock)
        {
            return (_streamingTokenBuffer.ToString(), _streamingReasoningBuffer.ToString(), _pendingTextMessageId, _pendingReasoningMessageId);
        }
    }

    public IReadOnlyList<AgentStreamEvent> DrainPendingStreamEvents()
    {
        lock (_bufferLock)
        {
            if (_pendingStreamEvents.Count == 0)
            {
                return Array.Empty<AgentStreamEvent>();
            }

            var events = _pendingStreamEvents.ToList();
            _pendingStreamEvents.Clear();
            return events;
        }
    }

    public void FlushBufferedEvents(ObservableCollection<ChatMessageViewModel> messages)
    {
        Queue<AgentStreamEvent> pendingEvents;
        lock (_bufferLock)
        {
            pendingEvents = new Queue<AgentStreamEvent>(_pendingStreamEvents);
            _pendingStreamEvents.Clear();
        }

        while (pendingEvents.Count > 0)
        {
            _streaming.Process(pendingEvents.Dequeue(), messages);
        }
    }

    public void FlushTokens(
        ObservableCollection<ChatMessageViewModel> messages,
        bool isDisplayed,
        Action requestScroll)
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
            _streaming.Process(new AgentStreamEvent.TextMessageContent(textMessageId, pendingTokens), messages);
            didFlush = true;
        }

        if (pendingReasoning.Length > 0 && reasoningMessageId is not null)
        {
            _streaming.Process(new AgentStreamEvent.ReasoningMessageContent(reasoningMessageId, pendingReasoning), messages);
            didFlush = true;
        }

        if (didFlush && isDisplayed)
        {
            requestScroll();
        }
    }

    public void FlushAll(
        ObservableCollection<ChatMessageViewModel> messages,
        bool isDisplayed,
        Action requestScroll)
    {
        FlushBufferedEvents(messages);
        FlushTokens(messages, isDisplayed, requestScroll);
    }

    public void ScheduleFlush(bool isDisplayed)
    {
        if (!isDisplayed)
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

        _dispatcher.BeginInvoke(DispatcherPriority.Background, StartFlushTimer);
    }

    public void StopFlushTimer()
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

    private void StartFlushTimer()
    {
        if (_streamingFlushTimer is not null)
        {
            return;
        }

        _streamingFlushTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = FlushInterval
        };
        _streamingFlushTimer.Tick += OnFlushTimerTick;
        _streamingFlushTimer.Start();
    }

    private void OnFlushTimerTick(object? sender, EventArgs e)
    {
        // Caller wires flush via delegate; timer only signals token drain.
        FlushTimerTick?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? FlushTimerTick;
}
