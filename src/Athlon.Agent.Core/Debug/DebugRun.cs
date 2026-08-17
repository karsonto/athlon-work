namespace Athlon.Agent.Core.Debug;

public sealed class DebugRun
{
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    public DebugPhase Phase { get; set; } = DebugPhase.Hypothesize;

    public List<DebugHypothesis> Hypotheses { get; set; } = [];

    public required string LogPath { get; set; }

    public string? ReproStepsMarkdown { get; set; }

    public string? RootCauseSummary { get; set; }

    public string? BugDescription { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsAwaitingUser =>
        Phase is DebugPhase.AwaitRepro or DebugPhase.AwaitVerify;

    public DebugRun Clone() => new()
    {
        Id = Id,
        SessionId = SessionId,
        Phase = Phase,
        Hypotheses = Hypotheses.Select(h => h with { }).ToList(),
        LogPath = LogPath,
        ReproStepsMarkdown = ReproStepsMarkdown,
        RootCauseSummary = RootCauseSummary,
        BugDescription = BugDescription,
        CreatedAt = CreatedAt
    };
}
