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

    /// <summary>
    /// Stricter per-tool result budgets, keyed by tool name (ordinal). Tools that emit large,
    /// short-lived payloads (Computer Use observations) would otherwise consume the global limit on
    /// every call. An entry with a non-positive bound in any dimension leaves that dimension at the
    /// global value.
    /// </summary>
    public Dictionary<string, ToolResultLimit> ToolResultOverrides { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// When true, <c>ui_tree</c> payloads in all but the newest
    /// <see cref="HistoryUiTreeRetention"/> Computer Use observations are replaced with a compact
    /// summary. Defaults to false: the stripping rule changes what the model sees, so it ships
    /// behind a switch and is enabled only after it is validated in real sessions.
    /// </summary>
    public bool PruneHistoricalUiTree { get; set; }

    /// <summary>Number of newest Computer Use observations whose full <c>ui_tree</c> stays in history.</summary>
    public int HistoryUiTreeRetention { get; set; } = 2;

    /// <summary>
    /// Resolves the effective result limits for <paramref name="toolName"/>, falling back to the
    /// global limits when no override is configured or the tool is unknown.
    /// </summary>
    public ToolResultLimit ResolveToolResultLimit(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName)
            || ToolResultOverrides.Count == 0
            || !ToolResultOverrides.TryGetValue(toolName!, out var limit))
        {
            return new ToolResultLimit(MaxToolResultLines, MaxToolResultBytes, MaxToolResultTokens);
        }

        return new ToolResultLimit(
            limit.MaxLines > 0 ? limit.MaxLines : MaxToolResultLines,
            limit.MaxBytes > 0 ? limit.MaxBytes : MaxToolResultBytes,
            limit.MaxTokens > 0 ? limit.MaxTokens : MaxToolResultTokens);
    }
}

/// <summary>Byte/line/token budget for a single tool result. Non-positive values mean "use global".</summary>
public sealed record ToolResultLimit(int MaxLines, int MaxBytes, int MaxTokens);

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
