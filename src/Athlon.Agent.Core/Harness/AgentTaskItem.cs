namespace Athlon.Agent.Core.Harness;

public static class AgentTaskStatuses
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Pending,
        InProgress,
        Completed,
        Cancelled
    };

    public static string Normalize(string? status) =>
        string.IsNullOrWhiteSpace(status)
            ? Pending
            : status.Trim().ToLowerInvariant();
}

public sealed class AgentTaskItem
{
    public string Id { get; set; } = "";

    public string Content { get; set; } = "";

    public string Status { get; set; } = AgentTaskStatuses.Pending;
}

public sealed class SessionTaskList
{
    public List<AgentTaskItem> Items { get; set; } = [];
}

public interface ISessionTaskListStore
{
    Task<SessionTaskList> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    Task ReplaceAsync(string sessionId, SessionTaskList list, CancellationToken cancellationToken = default);

    Task<SessionTaskList> ApplyMergeAsync(
        string sessionId,
        IReadOnlyList<AgentTaskItem> todos,
        bool merge,
        CancellationToken cancellationToken = default);
}
