using Athlon.Agent.Core.Events;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core;

public sealed class AgentTurnCallbacks
{
    /// <summary>Invoked when the in-memory session is replaced (e.g. after conversation compact).</summary>
    public Func<AgentSession, Task>? OnSessionUpdated { get; init; }

    public Func<AgentStreamEvent, Task>? OnStreamEvent { get; init; }

    public Func<SessionUsageSnapshot, Task>? OnUsageRecorded { get; init; }

    /// <summary>
    /// Requests an explicit decision for an Ask/approval-required tool. When absent,
    /// the invocation remains pending and is never executed.
    /// </summary>
    public Func<PendingToolApproval, CancellationToken, Task<ToolApprovalDecision>>? OnToolApprovalRequested { get; init; }

    public IAgentRunEventSink? EventSink { get; init; }
}
