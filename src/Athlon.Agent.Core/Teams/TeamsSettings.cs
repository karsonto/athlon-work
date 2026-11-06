namespace Athlon.Agent.Core.Teams;

public sealed class TeamsSettings
{
    /// <summary>When false, Teams UI/tools/orchestrator stay dormant (default).</summary>
    public bool Enabled { get; set; }

    public int MaxParallelWorkers { get; set; } = 3;

    public int DefaultMaxIterations { get; set; } = 3;

    public int AbsoluteMaxIterations { get; set; } = 10;

    public int MaxModelToolRounds { get; set; } = 24;
}
