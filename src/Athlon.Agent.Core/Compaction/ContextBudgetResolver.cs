using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Single entry point for resolving a context budget from the raw prompt surface plus whatever the
/// provider last measured.
///
/// <para>Two call sites used to compute this independently — the agent loop
/// (<c>CompactionTurnMiddleware</c>) and the UI's manual recalculation
/// (<c>SessionCompactionService.ComputeBudget</c>) — with different calibration multipliers and only
/// one of them honouring measured <c>prompt_tokens</c>. The composer occupancy meter therefore
/// disagreed with the pressure level depending on which path refreshed it last. Both now go through
/// here.</para>
/// </summary>
public static class ContextBudgetResolver
{
    /// <summary>
    /// Computes the budget and folds in the provider-reported prompt size when one is available.
    /// </summary>
    /// <param name="lastPromptTokens">
    /// Last <c>prompt_tokens</c> reported for this session, or null when nothing was measured yet.
    /// </param>
    public static ContextBudgetSnapshot Resolve(
        string environmentPrompt,
        IReadOnlyList<ToolDefinition> tools,
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings compactionSettings,
        ModelSettings modelSettings,
        double calibrationMultiplier = 1.0,
        string? runtimeContext = null,
        PromptOccupancyTokens? promptOccupancy = null,
        int? lastPromptTokens = null)
    {
        var budget = ContextBudgetCalculator.Compute(
            environmentPrompt,
            tools,
            messages,
            compactionSettings,
            modelSettings,
            calibrationMultiplier,
            runtimeContext,
            promptOccupancy);

        return ApplyPromptPressure(budget, lastPromptTokens, compactionSettings.PreferMeasuredPromptTokens);
    }

    /// <summary>
    /// Replaces the estimated history with the measured one when the provider reported how large
    /// the real prompt was.
    ///
    /// <para><paramref name="preferMeasured"/> is the default: the measurement is what the API
    /// actually received, so it is authoritative for utilization. The subtraction is safe because
    /// <c>TotalUtilization = (FixedOverhead + EstimatedHistory) / UsablePromptWindow</c>; when
    /// <c>EstimatedHistory = actual - FixedOverhead</c> the numerator reduces to <c>actual</c>, so
    /// any error in the <c>FixedOverhead</c> estimate cancels out.</para>
    ///
    /// <para>When <paramref name="preferMeasured"/> is false the historical "floor" semantics apply:
    /// the measurement raises the estimate but never lowers it, keeping the estimator conservative if
    /// the measured value is suspected to lag behind newly appended history.</para>
    /// </summary>
    public static ContextBudgetSnapshot ApplyPromptPressure(
        ContextBudgetSnapshot budget,
        int? lastPromptTokens,
        bool preferMeasured = true)
    {
        if (lastPromptTokens is not > 0)
        {
            return budget;
        }

        var historyFromActual = Math.Max(0, lastPromptTokens.Value - budget.FixedOverhead);
        if (!preferMeasured && historyFromActual <= budget.EstimatedHistory)
        {
            return budget;
        }

        return budget.WithHistoryEstimate(historyFromActual, budget.HistoryBudget);
    }
}
