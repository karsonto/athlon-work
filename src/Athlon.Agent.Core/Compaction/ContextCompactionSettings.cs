using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.Core.Compaction;

public sealed class ContextCompactionSettings
{
    /// <summary>Master switch. When false, proactive compaction is skipped (API overflow retry still runs).</summary>
    public bool Enabled { get; set; } = true;

    public int ContextWindowTokens { get; set; } = 131_072;

    /// <summary>
    /// When &gt; 0 with <see cref="ContextWindowTokens"/>, static (non-dynamic) compaction also triggers when
    /// estimated history tokens reach <c>ContextWindowTokens * CompactTriggerRatio</c>.
    /// </summary>
    public double CompactTriggerRatio { get; set; } = 0.7;

    public int TriggerMessages { get; set; } = 50;

    public int TriggerTokens { get; set; } = 80_000;

    /// <summary>Legacy keep message floor for static cutoff helpers; checkpoint path uses user token budgets.</summary>
    public int KeepMessages { get; set; } = 20;

    public int KeepTokens { get; set; }

    public bool OffloadBeforeCompact { get; set; } = true;

    /// <summary>
    /// When false (default), plain assistant replies omit <c>ReasoningContent</c> from API history
    /// and compaction token estimates. Assistant messages with <c>tool_calls</c> still include
    /// reasoning so tool loops can continue.
    /// </summary>
    public bool IncludeReasoningInModelContext { get; set; }

    public int MaxToolScreenshotsInModelContext { get; set; } = 2;

    /// <summary>
    /// Retained for settings.json deserialize compatibility only. Compaction always uses
    /// <see cref="ConversationCompactionDefaults.DefaultSummaryPrompt"/>.
    /// </summary>
    public string SummaryPrompt { get; set; } = ConversationCompactionDefaults.DefaultSummaryPrompt;

    public int MaxConversationCharsForSummary { get; set; } = 200_000;

    public int SummaryMaxTokens { get; set; } = 4_096;

    /// <summary>Max approx tokens of recent real user messages retained after a checkpoint (Codex-style).</summary>
    public int RetainRecentUserMaxTokens { get; set; } = 20_000;

    /// <summary>Tighter user retention budget for Overflow / Force checkpoints.</summary>
    public int OverflowRetainRecentUserMaxTokens { get; set; } = 8_000;

    /// <summary>Warn when a session has at least this many compaction summaries (quality may degrade).</summary>
    public int CompactionQualityWarnAfterCount { get; set; } = 2;

    public ToolResultEvictionSettings ToolResultEviction { get; set; } = new();

    public DynamicCompactionSettings DynamicCompaction { get; set; } = new();

    /// <summary>When true, Overflow summary LLM failure falls back to a deterministic template summary.</summary>
    public bool EnableTemplateSummaryOnOverflowFailure { get; set; } = true;

    /// <summary>
    /// When FixedOverhead / UsablePromptWindow is at or above this ratio, history compaction cannot
    /// free the window; callers should skip empty LLM summary attempts.
    /// </summary>
    public double FixedOverheadDominatesRatio { get; set; } = 0.85;

    /// <summary>
    /// Prefer the provider-reported <c>prompt_tokens</c> over the character-based estimate when
    /// resolving context utilization.
    /// </summary>
    public bool PreferMeasuredPromptTokens { get; set; } = true;

    public RequestHistoryHygieneSettings RequestHistoryHygiene { get; set; } = CreateDefaultHistoryHygiene();

    public ToolStormSettings ToolStorm { get; set; } = new();

    private static RequestHistoryHygieneSettings CreateDefaultHistoryHygiene()
    {
        var settings = new RequestHistoryHygieneSettings();
        var limit = new ToolResultLimit(
            ComputerUseSettingsDefaults.ObservationResultMaxLines,
            ComputerUseSettingsDefaults.ObservationResultMaxBytes,
            ComputerUseSettingsDefaults.ObservationResultMaxTokens);
        foreach (var toolName in ComputerUseToolNames.All)
        {
            settings.ToolResultOverrides[toolName] = limit;
        }

        return settings;
    }
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
