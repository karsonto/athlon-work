namespace Athlon.Agent.Core.Teams;

/// <summary>Per-task orchestration metrics written to metrics.json at terminal state.</summary>
public sealed class TeamTaskMetrics
{
    public string TaskId { get; set; } = "";

    public string RootSessionId { get; set; } = "";

    public string Status { get; set; } = "";

    public int LeaderTurns { get; set; }

    public int RepairRoundsUsed { get; set; }

    public int WorkerRuns { get; set; }

    public int SucceededSubtasks { get; set; }

    public int FailedSubtasks { get; set; }

    public int CancelledSubtasks { get; set; }

    public int ResultInvalidCount { get; set; }

    public int ResultNudgeCount { get; set; }

    public int ReportToolCount { get; set; }

    public int MaxParallelObserved { get; set; }

    public bool TimedOut { get; set; }

    public int WorkerToolRoundCapHits { get; set; }

    public long PromptTokens { get; set; }

    public long CompletionTokens { get; set; }

    public long DurationMs { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}
