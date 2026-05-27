namespace Athlon.Agent.Core.Compaction;

public interface IAutoCompactService
{
    Task<AgentSession> CompactAsync(AgentSession session, CancellationToken cancellationToken = default);
}
