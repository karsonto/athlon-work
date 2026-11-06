namespace Athlon.Agent.Core.Teams;

public enum TeamTaskStatus
{
    Pending,
    Running,
    AwaitingHuman,
    Succeeded,
    Failed,
    Cancelled
}

public enum TeamSubTaskStatus
{
    Pending,
    Ready,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    WaitingRedispatch
}

public sealed class TeamTask
{
    public string TaskId { get; set; } = "";

    public string TeamId { get; set; } = "";

    public string RootSessionId { get; set; } = "";

    public string Goal { get; set; } = "";

    public TeamTaskStatus Status { get; set; } = TeamTaskStatus.Pending;

    public string? LeaderSessionId { get; set; }

    public string? LeaderProfileId { get; set; }

    public string? FinalResult { get; set; }

    public string? Error { get; set; }

    public List<TeamSubTask> SubTasks { get; set; } = new();

    public Dictionary<string, TeamTraceNodeLayout> TraceLayout { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class TeamSubTask
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string AssigneeProfileId { get; set; } = "";

    public string? MemberSessionId { get; set; }

    public List<string> DependsOn { get; set; } = new();

    public TeamSubTaskStatus Status { get; set; } = TeamSubTaskStatus.Pending;

    public string Handoff { get; set; } = "";

    public List<string> ArtifactRefs { get; set; } = new();

    public int Iteration { get; set; } = 1;

    public int MaxIterations { get; set; } = 1;

    public string? RedispatchReason { get; set; }

    public string? PrevResultSummary { get; set; }

    public string? Result { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class TeamTraceNodeLayout
{
    public double X { get; set; }

    public double Y { get; set; }
}

public sealed class TeamTraceEvent
{
    public string Id { get; set; } = IdGen.NewId();

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public string Type { get; set; } = "";

    public string? NodeId { get; set; }

    public string? FromNodeId { get; set; }

    public string? ToNodeId { get; set; }

    public string? ProfileId { get; set; }

    public string? SubTaskId { get; set; }

    public int? Iteration { get; set; }

    public string? Message { get; set; }

    public Dictionary<string, object?> Data { get; set; } = new();
}
