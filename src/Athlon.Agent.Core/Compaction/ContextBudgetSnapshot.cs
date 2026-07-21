namespace Athlon.Agent.Core.Compaction;

public sealed record ContextBudgetSnapshot(
    int TotalWindow,
    int ReservedOutput,
    int FixedOverhead,
    int HistoryBudget,
    int EstimatedHistory,
    /// <summary>estimatedHistory / historyBudget — informational only; not used for pressure triggers.</summary>
    double HistoryUtilization)
{
    /// <summary>Estimated history + system/tools/margin — approximates total prompt tokens.</summary>
    public int EstimatedTotalPrompt => FixedOverhead + EstimatedHistory;

    /// <summary>Context window minus reserved completion tokens.</summary>
    public int UsablePromptWindow => Math.Max(1, TotalWindow - ReservedOutput);

    /// <summary>
    /// Share of the usable window consumed by the full prompt.
    /// This is the dynamic pressure metric used by <see cref="ContextPressureEvaluator"/>.
    /// </summary>
    public double TotalUtilization => (double)EstimatedTotalPrompt / UsablePromptWindow;

    public int AvailableHistory => Math.Max(0, HistoryBudget - EstimatedHistory);

    public ContextBudgetSnapshot WithHistoryEstimate(int estimatedHistory, int historyBudget)
    {
        var budget = historyBudget > 0 ? historyBudget : HistoryBudget;
        var historyUtilization = budget > 0 ? (double)estimatedHistory / budget : 1.0;
        return this with
        {
            HistoryBudget = budget,
            EstimatedHistory = estimatedHistory,
            HistoryUtilization = historyUtilization
        };
    }
}
