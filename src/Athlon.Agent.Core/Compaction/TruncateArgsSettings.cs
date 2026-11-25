namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// In-session truncation of oversized tool-call string arguments before compaction decisions.
/// </summary>
public sealed class TruncateArgsSettings
{
    public bool Enabled { get; set; } = true;

    public int MaxArgLength { get; set; } = 2_000;
}