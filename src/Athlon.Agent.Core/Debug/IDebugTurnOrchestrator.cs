namespace Athlon.Agent.Core.Debug;

public enum DebugContinuationKind
{
    Reproduced,
    VerifiedFixed,
    VerifiedNotFixed
}

public interface IDebugTurnOrchestrator
{
    bool IsAwaitingUser(string sessionId);

    Task<AgentSession> RunUserTurnAsync(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken);

    Task<AgentSession> ContinueAsync(
        AgentSession session,
        DebugContinuationKind continuation,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken);
}
