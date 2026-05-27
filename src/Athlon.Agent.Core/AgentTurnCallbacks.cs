namespace Athlon.Agent.Core;

public sealed class AgentTurnCallbacks
{
    public Func<ChatMessage, Task>? OnMessage { get; init; }

    public Func<AgentToolCall, Task>? OnToolStarted { get; init; }

    public Func<string, Task>? OnAssistantTextDelta { get; init; }
}
