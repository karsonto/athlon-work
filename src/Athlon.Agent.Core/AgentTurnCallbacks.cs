namespace Athlon.Agent.Core;

public sealed class AgentTurnCallbacks
{
    /// <summary>Invoked when the in-memory session is replaced (e.g. after conversation compact).</summary>
    public Func<AgentSession, Task>? OnSessionUpdated { get; init; }

    public Func<ChatMessage, Task>? OnMessage { get; init; }

    public Func<AgentToolCall, Task>? OnToolStarted { get; init; }

    public Func<string, Task>? OnAssistantTextDelta { get; init; }

    public Func<string, Task>? OnAssistantReasoningDelta { get; init; }

    public Func<StreamingToolCallDelta, Task>? OnAssistantToolCallDelta { get; init; }
}
