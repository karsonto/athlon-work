namespace Athlon.Agent.Core.SubAgents;

public interface ISubAgentSessionStore
{
    Task<SubAgentSessionBundle?> LoadAsync(
        string parentSessionId,
        string subSessionId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string parentSessionId,
        string subSessionId,
        SubAgentSessionBundle bundle,
        CancellationToken cancellationToken = default);
}
