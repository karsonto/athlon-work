namespace Athlon.Agent.Core.Compaction;

public sealed class RequestHistoryHygieneSettings
{
    public bool Enabled { get; set; } = true;

    public int MaxToolResultLines { get; set; } = 320;

    public int MaxToolResultBytes { get; set; } = 32 * 1024;

    public int MaxToolResultTokens { get; set; } = 8_000;

    public int MaxToolArgumentStringBytes { get; set; } = 8 * 1024;

    public int MaxToolArgumentStringTokens { get; set; } = 2_000;

    public int MaxArrayItems { get; set; } = 80;
}

public enum ToolStormScope
{
    Turn,
    Session
}

public sealed class ToolStormSettings
{
    public bool Enabled { get; set; } = true;

    public ToolStormScope Scope { get; set; } = ToolStormScope.Turn;

    public int WindowSize { get; set; } = 8;

    public int Threshold { get; set; } = 3;
}
