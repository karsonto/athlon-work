namespace Athlon.Agent.Core.SubAgents;

public interface ISubAgentTaskStore
{
    Task<SubAgentTaskRecord> CreateAsync(
        string parentSessionId,
        string sessionKey,
        string subSessionId,
        CancellationToken cancellationToken = default);

    Task<SubAgentTaskRecord?> GetAsync(string parentSessionId, string taskId, CancellationToken cancellationToken = default);

    Task UpdateAsync(string parentSessionId, SubAgentTaskRecord record, CancellationToken cancellationToken = default);
}
