namespace Athlon.Agent.Core.SubAgents;

public interface ISubAgentCompletionStore
{
    Task AppendAsync(string parentSessionId, PendingCompletion completion, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingCompletion>> DrainAsync(
        string parentSessionId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<int> PeekCountAsync(string parentSessionId, CancellationToken cancellationToken = default);
}
