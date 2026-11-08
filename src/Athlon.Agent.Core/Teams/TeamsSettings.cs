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

    public int MaxModelToolRounds { get; set; } = 24;

    /// <summary>
    /// Silent global safety valve: max Leader-driven repair/redispatch waves per task.
    /// Not exposed in Teams UI. When reached, Leader must ask_user or submit partial/failed.
    /// </summary>
    public int MaxRepairRounds { get; set; } = 10;

    /// <summary>When true, orchestrator writes subtask result summaries into team memory.</summary>
    public bool AutoWriteTeamMemory { get; set; } = true;
}
