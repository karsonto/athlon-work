namespace Athlon.Agent.Core.SubAgents;

public interface ISubAgentRegistry
{
    Task<SubAgentSessionEntry?> FindByLabelAsync(string parentSessionId, string label, CancellationToken cancellationToken = default);

    Task<SubAgentSessionEntry?> FindBySubSessionIdAsync(
        string parentSessionId,
        string subSessionId,
        CancellationToken cancellationToken = default);

    Task<SubAgentSessionEntry?> FindBySessionKeyAsync(string sessionKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubAgentSessionEntry>> ListAsync(string parentSessionId, CancellationToken cancellationToken = default);

    Task RegisterAsync(
        string parentSessionId,
        string subSessionId,
        SubAgentMetaFile meta,
        CancellationToken cancellationToken = default);

    Task UpdateLastActivityAsync(string parentSessionId, string subSessionId, CancellationToken cancellationToken = default);

    string GetSessionFilePath(string parentSessionId, string subSessionId);

    string GetSubAgentDirectory(string parentSessionId, string subSessionId);
}
