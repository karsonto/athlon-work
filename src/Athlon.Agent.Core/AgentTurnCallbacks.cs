using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core;

public sealed class AgentTurnCallbacks
{
    /// <summary>Invoked when the in-memory session is replaced (e.g. after conversation compact).</summary>
    public Func<AgentSession, Task>? OnSessionUpdated { get; init; }

    public Func<AgentStreamEvent, Task>? OnStreamEvent { get; init; }

    public Func<SessionUsageSnapshot, Task>? OnUsageRecorded { get; init; }
}
