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

    /// <summary>
    /// Upper bound on how many trailing messages the semantic cutoff protects.
    ///
    /// The protected tail is anchored at the last real user message, which is appended when a turn
    /// <em>starts</em>. A long agentic turn therefore accumulates every tool result after that
    /// anchor, and an unbounded protected tail would make those messages unsummarizable — compaction
    /// would re-summarize an already summarized prefix forever without reducing the payload.
    ///
    /// Capping the protected tail lets the cutoff move forward inside a long turn while a short
    /// conversation (fewer messages than this cap) keeps its existing behaviour exactly.
    /// Values below 1 are treated as unbounded.
    /// </summary>
    public int ProtectedTailMaxMessages { get; set; } = 40;

    /// <summary>
    /// Minimum token reduction required before paying for an LLM summarization call. When the
    /// projected post-compaction payload does not shrink by at least this much, compaction is
    /// skipped (and reported as not compacted) instead of burning a summary round trip.
    /// Values below 0 are treated as 0.
    /// </summary>
    public int MinCompactionSavingsTokens { get; set; } = 2_000;

    /// <summary>
    /// When the cutoff advances past the last real user message, re-attach that message verbatim
    /// after the summary so the active instruction survives the cut. Set false to rely on
    /// <c>must_preserve</c> in the summary prompt alone.
    /// </summary>
    public bool ReattachLatestUserMessage { get; set; } = true;

    /// <summary>
    /// Prefer the provider-reported <c>prompt_tokens</c> over the character-based estimate when
    /// resolving context utilization. The measured value is authoritative for what the API
    /// actually received, so the composer occupancy meter and pressure levels track reality rather
    /// than estimator drift. Set false to fall back to the "larger of measured and estimated"
    /// (floor) semantics.
    /// </summary>
    public bool PreferMeasuredPromptTokens { get; set; } = true;

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
