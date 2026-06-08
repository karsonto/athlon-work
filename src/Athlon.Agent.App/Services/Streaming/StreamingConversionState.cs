namespace Athlon.Agent.App.Services.Streaming;

/// <summary>AG-UI-style conversion state for assistant text, reasoning, and tool-call streams.</summary>
public sealed class StreamingConversionState
{
    private readonly HashSet<string> _startedTextStreams = new(StringComparer.Ordinal);
    private readonly HashSet<string> _endedTextStreams = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedReasoningStreams = new(StringComparer.Ordinal);
    private readonly HashSet<string> _endedReasoningStreams = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedToolCallIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _endedToolCallIds = new(StringComparer.Ordinal);

    public string? CurrentTextStreamId { get; private set; }

    public string? CurrentReasoningStreamId { get; private set; }

    public string? ActiveAssistantStreamId { get; private set; }

    public Dictionary<int, string> ToolStreamIndexToCallId { get; } = new();

    public bool HasStartedTextStream(string streamId) => _startedTextStreams.Contains(streamId);

    public bool HasEndedTextStream(string streamId) => _endedTextStreams.Contains(streamId);

    public bool HasActiveTextStream() =>
        CurrentTextStreamId is not null && !HasEndedTextStream(CurrentTextStreamId);

    public void StartTextStream(string streamId)
    {
        _startedTextStreams.Add(streamId);
        CurrentTextStreamId = streamId;
        ActiveAssistantStreamId = streamId;
    }

    public void EndTextStream(string streamId)
    {
        _endedTextStreams.Add(streamId);
        if (string.Equals(streamId, CurrentTextStreamId, StringComparison.Ordinal))
        {
            CurrentTextStreamId = null;
        }

        if (string.Equals(streamId, ActiveAssistantStreamId, StringComparison.Ordinal)
            && !HasActiveReasoningStream())
        {
            ActiveAssistantStreamId = null;
        }
    }

    public bool HasStartedReasoningStream(string streamId) => _startedReasoningStreams.Contains(streamId);

    public bool HasEndedReasoningStream(string streamId) => _endedReasoningStreams.Contains(streamId);

    public bool HasActiveReasoningStream() =>
        CurrentReasoningStreamId is not null && !HasEndedReasoningStream(CurrentReasoningStreamId);

    public void StartReasoningStream(string streamId)
    {
        _startedReasoningStreams.Add(streamId);
        CurrentReasoningStreamId = streamId;
        ActiveAssistantStreamId = streamId;
    }

    public void EndReasoningStream(string streamId)
    {
        _endedReasoningStreams.Add(streamId);
        if (string.Equals(streamId, CurrentReasoningStreamId, StringComparison.Ordinal))
        {
            CurrentReasoningStreamId = null;
        }

        if (string.Equals(streamId, ActiveAssistantStreamId, StringComparison.Ordinal)
            && !HasActiveTextStream())
        {
            ActiveAssistantStreamId = null;
        }
    }

    public bool HasStartedToolCall(string toolCallId) => _startedToolCallIds.Contains(toolCallId);

    public bool HasEndedToolCall(string toolCallId) => _endedToolCallIds.Contains(toolCallId);

    public void StartToolCall(string toolCallId) => _startedToolCallIds.Add(toolCallId);

    public void EndToolCall(string toolCallId) => _endedToolCallIds.Add(toolCallId);

    public IReadOnlyCollection<string> StartedTextStreams => _startedTextStreams;

    public IReadOnlyCollection<string> StartedReasoningStreams => _startedReasoningStreams;

    public void Reset()
    {
        _startedTextStreams.Clear();
        _endedTextStreams.Clear();
        _startedReasoningStreams.Clear();
        _endedReasoningStreams.Clear();
        _startedToolCallIds.Clear();
        _endedToolCallIds.Clear();
        ToolStreamIndexToCallId.Clear();
        CurrentTextStreamId = null;
        CurrentReasoningStreamId = null;
        ActiveAssistantStreamId = null;
    }
}
