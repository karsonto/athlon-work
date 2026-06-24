namespace Athlon.Agent.Core.Events;

public enum TurnOutcomeKind
{
    Completed,
    Cancelled,
    MaxToolRoundsReached,
    Failed
}

public sealed record TurnOutcome(TurnOutcomeKind Kind, Exception? Error = null);

public enum SessionMutationReason
{
    Compaction,
    ToolResult,
    UserMessage,
    AssistantMessage,
    Other
}

public abstract record AgentRunLifecycleEvent
{
    public sealed record TurnStarted(AgentRunContext Context) : AgentRunLifecycleEvent;

    public sealed record TurnFinished(AgentRunContext Context, AgentSession Session, TurnOutcome Outcome) : AgentRunLifecycleEvent;

    public sealed record SessionMutated(AgentSession Session, SessionMutationReason Reason) : AgentRunLifecycleEvent;
}
