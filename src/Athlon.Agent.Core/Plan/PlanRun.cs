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

    public List<PlanTodoItem> Todos { get; set; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Transient: set by <c>publish_plan</c> when it writes a document during the current turn.
    /// Reset at the start of a Draft (revision) turn. Without it, "did this turn produce a plan?"
    /// can only be answered by comparing markdown text, which misreads a re-published identical
    /// plan as "no output". Never persisted as a durable fact.
    /// </summary>
    public bool PublishedThisTurn { get; set; }

    /// <summary>
    /// Transient outcome of the most recent revision turn: true when the model published a new
    /// plan, false when it answered without publishing, null when the run was not revising.
    /// The UI reads this to tell the user the previous plan was kept.
    /// </summary>
    public bool? RevisionProducedNewPlan { get; set; }

    /// <summary>
    /// Transient: true while the run is inside a revision turn (including one resumed from a
    /// clarification question). Distinguishes "this Draft came from a Revise" from an Explore
    /// turn that happens to have markdown, which a markdown-presence check cannot tell apart.
    /// </summary>
    public bool IsRevisionTurn { get; set; }

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
        Todos = Todos.Select(t => new PlanTodoItem { Id = t.Id, Content = t.Content }).ToList(),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        PublishedThisTurn = PublishedThisTurn,
        RevisionProducedNewPlan = RevisionProducedNewPlan,
        IsRevisionTurn = IsRevisionTurn
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
