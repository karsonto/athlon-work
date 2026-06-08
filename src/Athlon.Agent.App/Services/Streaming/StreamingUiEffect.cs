using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services.Streaming;

public abstract record StreamingUiEffect
{
    public sealed record EnsureAssistantBubble(string StreamId) : StreamingUiEffect;

    public sealed record AppendAssistantText(string StreamId, string Delta) : StreamingUiEffect;

    public sealed record AppendAssistantReasoning(string StreamId, string Delta) : StreamingUiEffect;

    public sealed record SealAssistantBubble(string StreamId) : StreamingUiEffect;

    public sealed record ReleaseAssistantBubbleForToolBoundary(string StreamId) : StreamingUiEffect;

    public sealed record RemoveEmptyAssistantBubble(string StreamId) : StreamingUiEffect;

    public sealed record CompleteAssistantBubble(string StreamId, ChatMessage Message) : StreamingUiEffect;

    public sealed record MarkAssistantBubbleCancelled(string StreamId) : StreamingUiEffect;

    public sealed record EnsureToolBubble(int Index, string? ToolCallId, string? Name) : StreamingUiEffect;

    public sealed record UpdateToolBubble(int Index, string? ToolCallId, string? Name, string ArgumentsJson)
        : StreamingUiEffect;

    public sealed record PromoteToolBubbleToRunning(int? StreamIndex, AgentToolCall ToolCall) : StreamingUiEffect;

    public sealed record AddPendingToolBubble(AgentToolCall ToolCall) : StreamingUiEffect;

    public sealed record MarkStreamingToolCancelled(int Index) : StreamingUiEffect;

    public sealed record RequestScroll : StreamingUiEffect;

    public sealed record RequestScrollImmediate : StreamingUiEffect;
}
