namespace Athlon.Agent.Core.SubAgents;

public interface ISubAgentSessionManager
{
    Task<SpawnResult> SpawnAsync(
        string parentSessionId,
        string role,
        string? message,
        string? label,
        int? timeoutSeconds,
        CancellationToken cancellationToken = default);

    Task<SendResult> SendAsync(
        string parentSessionId,
        string? sessionKey,
        string? label,
        string message,
        int? timeoutSeconds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubAgentSessionEntry>> ListAsync(string parentSessionId, CancellationToken cancellationToken = default);

    Task<HistoryResult> HistoryAsync(string parentSessionId, string sessionKey, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingCompletion>> DrainCompletionsAsync(
        string parentSessionId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<int> PeekPendingCompletionsCountAsync(
        string parentSessionId,
        CancellationToken cancellationToken = default);

    Task<SubAgentTaskRecord?> GetTaskOutputAsync(
        string parentSessionId,
        string taskId,
        CancellationToken cancellationToken = default);
}
