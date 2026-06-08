namespace Athlon.Agent.Core.Streaming;

/// <summary>Converts model/runtime signals into AG-UI-aligned stream events.</summary>
public sealed class AgentStreamAdapter
{
    private readonly string _sessionId;
    private readonly string _runId;

    public AgentStreamAdapter(string sessionId, string runId)
    {
        _sessionId = sessionId;
        _runId = runId;
    }

    public AgentStreamConversionState State { get; } = new();

    public IReadOnlyList<AgentStreamEvent> CreateRunStarted() =>
        [new AgentStreamEvent.RunStarted(_sessionId, _runId)];

    public IReadOnlyList<AgentStreamEvent> OnTextDelta(string messageId, string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return [];
        }

        var events = new List<AgentStreamEvent>();
        EnsureActiveTextMessage(messageId, events);
        events.Add(new AgentStreamEvent.TextMessageContent(messageId, delta));
        return events;
    }

    public IReadOnlyList<AgentStreamEvent> OnReasoningDelta(string messageId, string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return [];
        }

        var events = new List<AgentStreamEvent>();
        EnsureActiveReasoningMessage(messageId, events);
        events.Add(new AgentStreamEvent.ReasoningMessageContent(messageId, delta));
        return events;
    }

    public IReadOnlyList<AgentStreamEvent> OnToolCallDelta(string messageId, StreamingToolCallDelta delta)
    {
        var events = new List<AgentStreamEvent>();
        EndActiveAssistantMessagesForToolBoundary(messageId, events);

        var toolCallId = string.IsNullOrWhiteSpace(delta.Id)
            ? $"stream-tool-{delta.Index}"
            : delta.Id;

        if (!State.HasStartedToolCall(toolCallId))
        {
            State.StartToolCall(toolCallId);
            events.Add(new AgentStreamEvent.ToolCallStart(
                toolCallId,
                delta.Name ?? "unknown",
                delta.Index));
        }

        State.ToolIndexToCallId[delta.Index] = toolCallId;
        if (!string.IsNullOrEmpty(delta.ArgumentsJson))
        {
            events.Add(new AgentStreamEvent.ToolCallArgs(toolCallId, delta.ArgumentsJson));
        }

        return events;
    }

    public IReadOnlyList<AgentStreamEvent> OnAssistantRoundCompleted(ChatMessage message)
    {
        var events = new List<AgentStreamEvent>();
        EndActiveAssistantMessagesForToolBoundary(message.Id, events);

        var toolCalls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
        for (var index = 0; index < toolCalls.Count; index++)
        {
            var toolCall = toolCalls[index];
            if (!State.HasStartedToolCall(toolCall.Id))
            {
                State.StartToolCall(toolCall.Id);
                events.Add(new AgentStreamEvent.ToolCallStart(toolCall.Id, toolCall.Name, index));
            }

            if (!State.HasEndedToolCall(toolCall.Id))
            {
                State.EndToolCall(toolCall.Id);
                events.Add(new AgentStreamEvent.ToolCallEnd(toolCall.Id));
            }
        }

        return events;
    }

    public IReadOnlyList<AgentStreamEvent> OnToolResult(ChatMessage toolMessage, AgentToolCall toolCall)
    {
        var events = new List<AgentStreamEvent>();
        if (!State.HasStartedToolCall(toolCall.Id))
        {
            State.StartToolCall(toolCall.Id);
            events.Add(new AgentStreamEvent.ToolCallStart(toolCall.Id, toolCall.Name, null));
        }

        if (!State.HasEndedToolCall(toolCall.Id))
        {
            State.EndToolCall(toolCall.Id);
            events.Add(new AgentStreamEvent.ToolCallEnd(toolCall.Id));
        }

        events.Add(new AgentStreamEvent.ToolCallResult(toolCall.Id, toolMessage.Content, toolMessage.Id));
        return events;
    }

    public IReadOnlyList<AgentStreamEvent> FinishRun()
    {
        var events = new List<AgentStreamEvent>();

        foreach (var messageId in State.StartedTextMessages)
        {
            if (!State.HasEndedTextMessage(messageId))
            {
                events.Add(new AgentStreamEvent.TextMessageEnd(messageId));
                State.EndTextMessage(messageId);
            }
        }

        foreach (var messageId in State.StartedReasoningMessages)
        {
            if (!State.HasEndedReasoningMessage(messageId))
            {
                events.Add(new AgentStreamEvent.ReasoningMessageEnd(messageId));
                State.EndReasoningMessage(messageId);
            }
        }

        foreach (var toolCallId in State.StartedToolCallIds)
        {
            if (!State.HasEndedToolCall(toolCallId))
            {
                events.Add(new AgentStreamEvent.ToolCallEnd(toolCallId));
                State.EndToolCall(toolCallId);
            }
        }

        State.ClearActiveAssistantMessage();
        events.Add(new AgentStreamEvent.RunFinished(_sessionId, _runId));
        return events;
    }

    private void EnsureActiveTextMessage(string messageId, List<AgentStreamEvent> events)
    {
        if (State.HasActiveTextMessage() && string.Equals(State.CurrentTextMessageId, messageId, StringComparison.Ordinal))
        {
            return;
        }

        if (!State.HasStartedTextMessage(messageId))
        {
            State.StartTextMessage(messageId);
            events.Add(new AgentStreamEvent.TextMessageStart(messageId, "assistant"));
        }
    }

    private void EnsureActiveReasoningMessage(string messageId, List<AgentStreamEvent> events)
    {
        if (!State.HasStartedTextMessage(messageId))
        {
            State.StartTextMessage(messageId);
            events.Add(new AgentStreamEvent.TextMessageStart(messageId, "assistant"));
        }

        if (State.HasActiveReasoningMessage()
            && string.Equals(State.CurrentReasoningMessageId, messageId, StringComparison.Ordinal))
        {
            return;
        }

        if (!State.HasStartedReasoningMessage(messageId))
        {
            State.StartReasoningMessage(messageId);
            events.Add(new AgentStreamEvent.ReasoningMessageStart(messageId, "reasoning"));
        }
    }

    private void EndActiveAssistantMessagesForToolBoundary(string messageId, List<AgentStreamEvent> events)
    {
        if (State.HasActiveTextMessage()
            && string.Equals(State.CurrentTextMessageId, messageId, StringComparison.Ordinal))
        {
            events.Add(new AgentStreamEvent.TextMessageEnd(messageId));
            State.EndTextMessage(messageId);
        }

        if (State.HasActiveReasoningMessage()
            && string.Equals(State.CurrentReasoningMessageId, messageId, StringComparison.Ordinal))
        {
            events.Add(new AgentStreamEvent.ReasoningMessageEnd(messageId));
            State.EndReasoningMessage(messageId);
        }

        if (string.Equals(State.ActiveAssistantMessageId, messageId, StringComparison.Ordinal))
        {
            State.ClearActiveAssistantMessage();
        }
    }
}
