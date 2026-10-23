namespace Athlon.Agent.Core.Storage;

public interface ISessionStorageRepository
{
    Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default);
    Task SaveSessionOffThreadAsync(AgentSession session, CancellationToken cancellationToken = default);
    Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<SessionIndexEntry?> LoadSessionIndexEntryAsync(string sessionId, CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default);
}
