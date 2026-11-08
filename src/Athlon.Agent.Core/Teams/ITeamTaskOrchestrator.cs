namespace Athlon.Agent.Core.Teams;

public sealed class TeamTaskStartRequest
{
    public required string TeamId { get; init; }

    public required string RootSessionId { get; init; }

    public required string Goal { get; init; }

    public string? WorkspacePath { get; init; }

    public string? ModelName { get; init; }
}

public sealed class TeamTaskStartResult
{
    public required TeamTask Task { get; init; }

    public string? Summary { get; init; }

    public bool Ok { get; init; }

    public string? Error { get; init; }

    public static TeamTaskStartResult Success(TeamTask task, string? summary = null) =>
        new() { Task = task, Summary = summary, Ok = true };

    public static TeamTaskStartResult Fail(string error, TeamTask? task = null) =>
        new() { Task = task ?? new TeamTask(), Error = error, Ok = false };
}

public interface ITeamTaskOrchestrator
{
    /// <summary>
    /// Creates and persists a Running task (with <c>task.started</c> trace) without executing members.
    /// Callers that need a live Trace window should open it after this returns, then call
    /// <see cref="RunToCompletionAsync"/>.
    /// </summary>
    Task<TeamTaskStartResult> BeginAsync(TeamTaskStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>Begins a task and runs it to completion (schedule / fire-and-forget callers).</summary>
    Task<TeamTaskStartResult> StartAsync(TeamTaskStartRequest request, CancellationToken cancellationToken = default);

    Task<TeamTask?> GetTaskAsync(string rootSessionId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Outer-loop orchestration: Leader turns enqueue workers; workers run in parallel up to
    /// MaxParallelWorkers; after workers finish the Leader is re-invoked until submit / ask_user / fail.
    /// </summary>
    /// <param name="humanAnswer">When resuming from <see cref="TeamTaskStatus.AwaitingHuman"/>.</param>
    /// <param name="progress">Optional live step reporter for Chat (Leader/Worker completions).</param>
    Task<TeamTask> RunToCompletionAsync(
        string rootSessionId,
        string taskId,
        CancellationToken cancellationToken = default,
        string? humanAnswer = null,
        IProgress<TeamRunProgress>? progress = null);
}

public interface ITeamMemberRunExecutor
{
    Task<TeamMemberRunOutcome> ExecuteAsync(
        TeamTask task,
        AgentProfile profile,
        string memberSessionId,
        string message,
        bool isLeader,
        CancellationToken cancellationToken = default);
}

public sealed class TeamMemberRunOutcome
{
    public bool Ok { get; init; }

    public string ResponseText { get; init; } = "";

    public string? Error { get; init; }

    public AgentSession? Session { get; init; }

    public static TeamMemberRunOutcome Success(string text, AgentSession session) =>
        new() { Ok = true, ResponseText = text, Session = session };

    public static TeamMemberRunOutcome Fail(string error) =>
        new() { Ok = false, Error = error, ResponseText = error };
}
