namespace Athlon.Agent.Core.Streaming;

/// <summary>AG-UI-aligned stream events emitted by <see cref="AgentStreamAdapter"/>.</summary>
public abstract record AgentStreamEvent
{
    public sealed record RunStarted(string SessionId, string RunId) : AgentStreamEvent;

    public sealed record RunFinished(string SessionId, string RunId) : AgentStreamEvent;

    public sealed record TextMessageStart(string MessageId, string Role) : AgentStreamEvent;

    public sealed record TextMessageContent(string MessageId, string Delta) : AgentStreamEvent;

    public sealed record TextMessageEnd(string MessageId) : AgentStreamEvent;

    public sealed record ReasoningMessageStart(string MessageId, string Role) : AgentStreamEvent;

    public sealed record ReasoningMessageContent(string MessageId, string Delta) : AgentStreamEvent;

    public sealed record ReasoningMessageEnd(string MessageId) : AgentStreamEvent;

    public sealed record ToolCallStart(string ToolCallId, string ToolName, int? Index) : AgentStreamEvent;

    public sealed record ToolCallArgs(string ToolCallId, string Delta) : AgentStreamEvent;

    public sealed record ToolCallEnd(string ToolCallId) : AgentStreamEvent;

    public sealed record ToolCallResult(string ToolCallId, string Content, string MessageId) : AgentStreamEvent;

    /// <summary>Incremental stdout/stderr output while a tool is still running.</summary>
    public sealed record ToolCallOutput(string ToolCallId, string Delta) : AgentStreamEvent;

    /// <summary>Non-streaming persisted messages (compaction notices, fallbacks).</summary>
    public sealed record ChatMessageAppended(ChatMessage Message) : AgentStreamEvent;

    public sealed record ClearEmptyAssistantPlaceholder : AgentStreamEvent;

    public sealed record UsageRecorded(SessionUsageSnapshot Snapshot) : AgentStreamEvent;

    public sealed record ContextHygieneApplied(int EstimatedSavingsTokens) : AgentStreamEvent;
}
