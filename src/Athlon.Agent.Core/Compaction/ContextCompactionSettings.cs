namespace Athlon.Agent.Core.Compaction;

public sealed class ContextCompactionSettings
{
    public int ContextWindowTokens { get; set; } = 256_000;

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
    /// When false (default), assistant <c>ReasoningContent</c> is omitted from API history and excluded from compaction token estimates.
    /// UI and <c>conversation.jsonl</c> still persist reasoning for display.
    /// </summary>
    public bool IncludeReasoningInModelContext { get; set; }

    public string SummaryPrompt { get; set; } = ConversationCompactionDefaults.DefaultSummaryPrompt;

    public int MaxConversationCharsForSummary { get; set; } = 200_000;

    public int SummaryMaxTokens { get; set; } = 4_096;

    public TruncateArgsSettings TruncateArgs { get; set; } = new();

    public ToolResultEvictionSettings ToolResultEviction { get; set; } = new();

    public DynamicCompactionSettings DynamicCompaction { get; set; } = new();
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
