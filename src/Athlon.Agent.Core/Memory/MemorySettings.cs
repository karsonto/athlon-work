namespace Athlon.Agent.Core.Memory;

public enum MemoryInlinePromptMode
{
    None,
    Preview,
    Full
}

public sealed class MemorySettings
{
    /// <summary>Minimum gap between two consolidation runs.</summary>
    public TimeSpan ConsolidationMinGap { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Daily files older than this many days are archived.</summary>
    public int DailyFileRetentionDays { get; set; } = 90;

    /// <summary>Max tokens for the consolidated MEMORY.md (fed to LLM as token budget).</summary>
    public int MaxMemoryTokens { get; set; } = 4000;

    /// <summary>Max tokens for the flush/summary LLM call output.</summary>
    public int SummaryMaxTokens { get; set; } = 1024;

    /// <summary>Max characters of conversation to include in flush prompt.</summary>
    public int MaxFlushConversationChars { get; set; } = 80_000;

    /// <summary>Subdirectory name under the app data root.</summary>
    public string MemoryDirName { get; set; } = "memory";

    /// <summary>Name of the curated memory file.</summary>
    public string CuratedFileName { get; set; } = "MEMORY.md";

    /// <summary>Name of the consolidation watermark file.</summary>
    public string WatermarkFileName { get; set; } = ".consolidation_state";

    /// <summary>Names of memory directories/files excluded from workspace tools.</summary>
    public List<string> ExcludePatterns { get; set; } = new() { "memory/", "MEMORY.md" };

    /// <summary>How much of MEMORY.md to inject into the system prompt when Coding is enabled.</summary>
    public MemoryInlinePromptMode InlinePromptMode { get; set; } = MemoryInlinePromptMode.None;

    /// <summary>Max characters to inline when <see cref="InlinePromptMode"/> is Preview.</summary>
    public int MaxInlineMemoryChars { get; set; } = 800;
}
