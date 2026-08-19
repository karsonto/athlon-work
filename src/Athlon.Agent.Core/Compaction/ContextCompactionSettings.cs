namespace Athlon.Agent.Core.Compaction;

public sealed class ContextCompactionSettings
{
    /// <summary>Master switch. When false, proactive compaction is skipped (API overflow retry still runs).</summary>
    public bool Enabled { get; set; }

    public int ContextWindowTokens { get; set; } = 65_535;

    /// <summary>
    /// When &gt; 0 with <see cref="ContextWindowTokens"/>, compaction also triggers when estimated
    /// history tokens reach <c>ContextWindowTokens * CompactTriggerRatio</c> (whichever token threshold is higher vs <see cref="TriggerTokens"/>).
    /// </summary>
    public double CompactTriggerRatio { get; set; } = 0.7;

    public int TriggerMessages { get; set; } = 50;

    public int TriggerTokens { get; set; } = 80_000;

    public int KeepMessages { get; set; } = 20;

    public int KeepTokens { get; set; } = 0;

    public bool OffloadBeforeCompact { get; set; } = true;

    /// <summary>
    /// When false (default), plain assistant replies omit <c>ReasoningContent</c> from API history
    /// and compaction token estimates. Assistant messages with <c>tool_calls</c> still include
    /// reasoning so tool loops can continue. UI and <c>conversation.jsonl</c> persist reasoning for display.
    /// </summary>
    public bool IncludeReasoningInModelContext { get; set; }

    /// <summary>
    /// Max Computer Use tool screenshots kept in the model API payload (newest first).
    /// User-uploaded images are not capped. Also used by history token pressure estimates.
    /// Values below 0 are treated as 0.
    /// </summary>
    public int MaxToolScreenshotsInModelContext { get; set; } = 2;

    public string SummaryPrompt { get; set; } = ConversationCompactionDefaults.DefaultSummaryPrompt;

    public int MaxConversationCharsForSummary { get; set; } = 200_000;

    public int SummaryMaxTokens { get; set; } = 4_096;

    public TruncateArgsSettings TruncateArgs { get; set; } = new();

    public ToolResultEvictionSettings ToolResultEviction { get; set; } = new();

    public DynamicCompactionSettings DynamicCompaction { get; set; } = new();

    /// <summary>
    /// When true, if overflow retry is skipped (payload not reduced), perform a single
    /// middle-cut compaction: keep head/tail windows and summarize the middle span.
    /// </summary>
    public bool MiddleCutOnRetrySkipped { get; set; } = true;

    /// <summary>Number of earliest conversation messages to preserve during middle-cut compaction.</summary>
    public int MiddleCutKeepHeadMessages { get; set; } = 2;

    /// <summary>Number of latest conversation messages to preserve during middle-cut compaction.</summary>
    public int MiddleCutKeepTailMessages { get; set; } = 12;

    /// <summary>Max middle-cut attempts per run to avoid repeated reshaping.</summary>
    public int MiddleCutMaxPerRun { get; set; } = 1;

    public RequestHistoryHygieneSettings RequestHistoryHygiene { get; set; } = new();

    public ToolStormSettings ToolStorm { get; set; } = new();
}

public sealed class TruncateArgsSettings
{
    public bool Enabled { get; set; } = true;

    public int TriggerMessages { get; set; } = 25;

    public int TriggerTokens { get; set; } = 40_000;

    public int KeepMessages { get; set; } = 20;

    public int KeepTokens { get; set; } = 0;

    public int MaxArgLength { get; set; } = 2_000;

    public string TruncationText { get; set; } = "...(argument truncated)";
}

public sealed class ToolResultEvictionSettings
{
    public bool Enabled { get; set; } = true;

    public int MaxResultChars { get; set; } = 80_000;

    public int PreviewChars { get; set; } = 2_000;

    public List<string> ExcludedToolNames { get; set; } =
    [
        "file_write",
        "file_edit",
        "grep_files",
        "glob_files",
        "file_list"
    ];
}
