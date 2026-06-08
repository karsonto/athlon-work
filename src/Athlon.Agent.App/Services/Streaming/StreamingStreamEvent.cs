using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services.Streaming;

public abstract record StreamingStreamEvent
{
    public sealed record TextDelta(string Content) : StreamingStreamEvent;

    public sealed record ReasoningDelta(string Content) : StreamingStreamEvent;

    public sealed record ToolCallDelta(StreamingToolCallDelta Delta) : StreamingStreamEvent;

    public sealed record ToolExecutionStarted(AgentToolCall ToolCall) : StreamingStreamEvent;

    public sealed record AssistantMessagePersisted(ChatMessage Message) : StreamingStreamEvent;

    public sealed record ClearEmptyAssistantPlaceholder : StreamingStreamEvent;

    public sealed record TurnReset : StreamingStreamEvent;

    public sealed record TurnFinalize : StreamingStreamEvent;
}
