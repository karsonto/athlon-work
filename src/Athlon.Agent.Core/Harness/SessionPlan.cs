namespace Athlon.Agent.Core.Harness;

public static class SessionPlanStatuses
{
    public const string Draft = "draft";
    public const string AwaitingConfirmation = "awaiting_confirmation";
    public const string Approved = "approved";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Draft,
        AwaitingConfirmation,
        Approved
    };

    public static string Normalize(string? status) =>
        string.IsNullOrWhiteSpace(status)
            ? Draft
            : status.Trim().ToLowerInvariant();
}

public sealed class SessionPlanTodoItem
{
    public string Id { get; set; } = "";

    public string Content { get; set; } = "";
}

public sealed class SessionPlan
{
    public string Title { get; set; } = "";

    public string Overview { get; set; } = "";

    public string Body { get; set; } = "";

    public List<SessionPlanTodoItem> Todos { get; set; } = [];

    public string Status { get; set; } = SessionPlanStatuses.Draft;

    public string UpdatedAt { get; set; } = "";

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Title)
        || !string.IsNullOrWhiteSpace(Overview)
        || !string.IsNullOrWhiteSpace(Body);
}

public interface ISessionPlanStore
{
    Task<SessionPlan> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SaveAsync(string sessionId, SessionPlan plan, CancellationToken cancellationToken = default);
}

public interface IPlanChangedNotifier
{
    event Action<string>? PlanChanged;

    void Notify(string sessionId);
}
