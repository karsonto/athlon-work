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
    Task<TeamTaskStartResult> StartAsync(TeamTaskStartRequest request, CancellationToken cancellationToken = default);

    Task<TeamTask?> GetTaskAsync(string rootSessionId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>Runs the Leader planning turn and serial worker loop for P0.</summary>
    Task<TeamTask> RunToCompletionAsync(string rootSessionId, string taskId, CancellationToken cancellationToken = default);
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
