namespace Athlon.Agent.Core.Streaming;

/// <summary>AG-UI-style conversion state keyed by persistent messageId.</summary>
public sealed class AgentStreamConversionState
{
    private readonly HashSet<string> _startedTextMessages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _endedTextMessages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedReasoningMessages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _endedReasoningMessages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedToolCallIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _endedToolCallIds = new(StringComparer.Ordinal);

    public string? CurrentTextMessageId { get; private set; }

    public string? CurrentReasoningMessageId { get; private set; }

    public string? ActiveAssistantMessageId { get; private set; }

    public Dictionary<int, string> ToolIndexToCallId { get; } = new();

    public bool HasStartedTextMessage(string messageId) => _startedTextMessages.Contains(messageId);

    public bool HasEndedTextMessage(string messageId) => _endedTextMessages.Contains(messageId);

    public bool HasActiveTextMessage() =>
        CurrentTextMessageId is not null && !HasEndedTextMessage(CurrentTextMessageId);

    public void StartTextMessage(string messageId)
    {
        _startedTextMessages.Add(messageId);
        CurrentTextMessageId = messageId;
        ActiveAssistantMessageId = messageId;
    }

    public void EndTextMessage(string messageId)
    {
        _endedTextMessages.Add(messageId);
        if (string.Equals(messageId, CurrentTextMessageId, StringComparison.Ordinal))
        {
            CurrentTextMessageId = null;
        }

        if (string.Equals(messageId, ActiveAssistantMessageId, StringComparison.Ordinal)
            && !HasActiveReasoningMessage())
        {
            ActiveAssistantMessageId = null;
        }
    }

    public bool HasStartedReasoningMessage(string messageId) => _startedReasoningMessages.Contains(messageId);

    public bool HasEndedReasoningMessage(string messageId) => _endedReasoningMessages.Contains(messageId);

    public bool HasActiveReasoningMessage() =>
        CurrentReasoningMessageId is not null && !HasEndedReasoningMessage(CurrentReasoningMessageId);

    public void StartReasoningMessage(string messageId)
    {
        _startedReasoningMessages.Add(messageId);
        CurrentReasoningMessageId = messageId;
        ActiveAssistantMessageId = messageId;
    }

    public void EndReasoningMessage(string messageId)
    {
        _endedReasoningMessages.Add(messageId);
        if (string.Equals(messageId, CurrentReasoningMessageId, StringComparison.Ordinal))
        {
            CurrentReasoningMessageId = null;
        }

        if (string.Equals(messageId, ActiveAssistantMessageId, StringComparison.Ordinal)
            && !HasActiveTextMessage())
        {
            ActiveAssistantMessageId = null;
        }
    }

    public bool HasStartedToolCall(string toolCallId) => _startedToolCallIds.Contains(toolCallId);

    public bool HasEndedToolCall(string toolCallId) => _endedToolCallIds.Contains(toolCallId);

    public void StartToolCall(string toolCallId) => _startedToolCallIds.Add(toolCallId);

    public void EndToolCall(string toolCallId) => _endedToolCallIds.Add(toolCallId);

    public IReadOnlyCollection<string> StartedTextMessages => _startedTextMessages;

    public IReadOnlyCollection<string> StartedReasoningMessages => _startedReasoningMessages;

    public IReadOnlyCollection<string> StartedToolCallIds => _startedToolCallIds;

    public void ClearActiveAssistantMessage() => ActiveAssistantMessageId = null;

    public void Reset()
    {
        _startedTextMessages.Clear();
        _endedTextMessages.Clear();
        _startedReasoningMessages.Clear();
        _endedReasoningMessages.Clear();
        _startedToolCallIds.Clear();
        _endedToolCallIds.Clear();
        ToolIndexToCallId.Clear();
        CurrentTextMessageId = null;
        CurrentReasoningMessageId = null;
        ActiveAssistantMessageId = null;
    }
}
