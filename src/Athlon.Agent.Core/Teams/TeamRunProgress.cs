namespace Athlon.Agent.Core.Teams;

public enum TeamRunProgressKind
{
    /// <summary>Emitted right after Begin so Chat can show a Trace deep-link immediately.</summary>
    TaskStarted,
    LeaderTurnCompleted,
    WorkerCompleted,
    WorkerFailed,
    WorkerCancelled
}

/// <summary>
/// Progress event raised while a Team task runs to completion.
/// Used by Chat to append live step bubbles and refresh FilesChanged cards.
/// </summary>
public sealed class TeamRunProgress
{
    public required TeamRunProgressKind Kind { get; init; }

    public required string RootSessionId { get; init; }

    public required string TaskId { get; init; }

    public string? SubTaskId { get; init; }

    public string Title { get; init; } = "";

    public string? ProfileId { get; init; }

    public int Iteration { get; init; } = 1;

    public string StatusLabel { get; init; } = "";

    /// <summary>Full model response for this step (## Result body when available).</summary>
    public string ResponseText { get; init; } = "";

    public string? Error { get; init; }
}
