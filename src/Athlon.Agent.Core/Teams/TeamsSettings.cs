namespace Athlon.Agent.Core.Teams;

public sealed class TeamsSettings
{
    /// <summary>When false, Teams UI/tools/orchestrator stay dormant (default).</summary>
    public bool Enabled { get; set; }

    /// <summary>Last Team selected in Chat Teams mode.</summary>
    public string? SelectedTeamId { get; set; }

    public int MaxParallelWorkers { get; set; } = 3;

    /// <summary>Obsolete: ignored. Per-worker MaxIterations is no longer applied.</summary>
    [Obsolete("Ignored at runtime. Redispatch uses MaxRepairRounds only.")]
    public int DefaultMaxIterations { get; set; } = 3;

    /// <summary>Obsolete: ignored. Per-worker MaxIterations is no longer applied.</summary>
    [Obsolete("Ignored at runtime. Redispatch uses MaxRepairRounds only.")]
    public int AbsoluteMaxIterations { get; set; } = 10;

    /// <summary>
    /// Cap for Leader model↔tool rounds only. Workers ignore this and run without a round limit
    /// (wall-clock <see cref="WorkerRunTimeoutSeconds"/> still applies).
    /// </summary>
    public int MaxModelToolRounds { get; set; } = 24;

    /// <summary>
    /// Silent global safety valve: max Leader-driven create/redispatch scheduling waves per task.
    /// Not exposed in Teams UI. When reached, Leader must ask_user or submit partial/failed.
    /// </summary>
    public int MaxRepairRounds { get; set; } = 10;

    /// <summary>Silent cap on Leader outer-loop turns per Team Task (not exposed in UI).</summary>
    public int MaxLeaderTurnsPerTask { get; set; } = 30;

    /// <summary>Per-Worker wall-clock timeout in seconds (silent safety valve). Default 12h.</summary>
    public int WorkerRunTimeoutSeconds { get; set; } = 43_200;

    /// <summary>Max empty-stop nudge Leader turns before Incomplete Failed.</summary>
    public int MaxEmptyStopNudges { get; set; } = 3;

    /// <summary>When true, orchestrator writes subtask result summaries into team memory.</summary>
    public bool AutoWriteTeamMemory { get; set; } = true;
}
