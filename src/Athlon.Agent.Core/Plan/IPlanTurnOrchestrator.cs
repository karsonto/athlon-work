namespace Athlon.Agent.Core.Plan;

public enum PlanContinuationKind
{
    Build,
    Revise
}

public interface IPlanTurnOrchestrator
{
    bool IsAwaitingUser(string sessionId);

    Task<AgentSession> RunUserTurnAsync(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken);

    Task<AgentSession> ContinueAsync(
        AgentSession session,
        PlanContinuationKind continuation,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken);
}
