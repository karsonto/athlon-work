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
    /// Cap for Leader model↔tool rounds. Workers use <see cref="MaxWorkerModelToolRounds"/> instead.
    /// </summary>
    public int MaxModelToolRounds { get; set; } = 24;

    /// <summary>
    /// Soft cap for Worker model↔tool rounds. 0 = unlimited (wall-clock
    /// <see cref="WorkerRunTimeoutSeconds"/> still applies). Default 32.
    /// </summary>
    public int MaxWorkerModelToolRounds { get; set; } = 32;

    /// <summary>
    /// Global task wall-clock budget in seconds (0 = unlimited). Combined with
    /// <c>TeamDefinition.MaxTaskDurationSeconds</c> via min of positive values.
    /// </summary>
    public int MaxTaskDurationSeconds { get; set; }

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

    /// <summary>
    /// How many times a Worker is nudged to emit a structured ## Result before the run synthesizes
    /// <c>status: needs_leader</c> from the raw text.
    /// </summary>
    public int MaxWorkerResultNudges { get; set; } = 2;

    /// <summary>
    /// When true, Leader kickoff/follow-up prompts suggest an implement → verify wave for code goals.
    /// </summary>
    public bool SuggestImplementVerifyWave { get; set; } = true;

    /// <summary>
    /// When true, <c>team_submit_result(disposition=completed)</c> requires a Succeeded subtask with
    /// <c>verdict: pass</c> for code-like goals (or any task that already used verdict semantics).
    /// </summary>
    public bool RequireVerifyPassForCodeGoals { get; set; } = true;

    /// <summary>When true, orchestrator writes subtask result summaries into team memory.</summary>
    public bool AutoWriteTeamMemory { get; set; } = true;
}
