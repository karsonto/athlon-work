namespace Athlon.Agent.Core.Plan;

public sealed class PlanTodoItem
{
    public string Id { get; set; } = "";

    public string Content { get; set; } = "";
}

public sealed class PlanRun
{
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    public PlanPhase Phase { get; set; } = PlanPhase.Explore;

    public string Status { get; set; } = PlanRunStatuses.Draft;

    public string? Goal { get; set; }

    public string? Title { get; set; }

    public string? Overview { get; set; }

    public string? PlanMarkdown { get; set; }

    public string? PlanPath { get; set; }

    public List<PlanTodoItem> Todos { get; set; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsAwaitingUser => Phase.IsAwaitingUser();

    public bool HasPlanContent =>
        !string.IsNullOrWhiteSpace(PlanMarkdown)
        || !string.IsNullOrWhiteSpace(Title);

    public PlanRun Clone() => new()
    {
        Id = Id,
        SessionId = SessionId,
        Phase = Phase,
        Status = Status,
        Goal = Goal,
        Title = Title,
        Overview = Overview,
        PlanMarkdown = PlanMarkdown,
        PlanPath = PlanPath,
        Todos = Todos.Select(t => new PlanTodoItem { Id = t.Id, Content = t.Content }).ToList(),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };
}

public static class PlanRunStatuses
{
    public const string Draft = "draft";
    public const string AwaitingClarification = "awaiting_clarification";
    public const string AwaitingConfirmation = "awaiting_confirmation";
    public const string Approved = "approved";

    public static string Normalize(string? status) =>
        string.IsNullOrWhiteSpace(status)
            ? Draft
            : status.Trim().ToLowerInvariant() switch
            {
                "approved" => Approved,
                "awaiting_confirmation" or "awaiting" or "ready" => AwaitingConfirmation,
                "awaiting_clarification" or "clarifying" => AwaitingClarification,
                _ => Draft
            };
}
