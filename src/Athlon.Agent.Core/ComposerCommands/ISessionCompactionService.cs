namespace Athlon.Agent.Core.ComposerCommands;

public interface ISessionCompactionService
{
    Task<SessionCompactionResult> CompactManuallyAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);
}

public sealed record SessionCompactionResult(
    AgentSession Session,
    bool Compacted,
    string StatusMessage);
