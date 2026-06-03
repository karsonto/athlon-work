namespace Athlon.Agent.Core.Compaction;

public sealed record ContextBudgetSnapshot(
    int TotalWindow,
    int ReservedOutput,
    int FixedOverhead,
    int HistoryBudget,
    int EstimatedHistory,
    double Utilization)
{
    /// <summary>Estimated history + system/tools/margin — approximates total prompt tokens.</summary>
    public int EstimatedTotalPrompt => FixedOverhead + EstimatedHistory;

    /// <summary>Context window minus reserved completion tokens.</summary>
    public int UsablePromptWindow => Math.Max(1, TotalWindow - ReservedOutput);

    /// <summary>Share of the usable window consumed by the full prompt (dynamic pressure metric).</summary>
    public double TotalUtilization => (double)EstimatedTotalPrompt / UsablePromptWindow;

    public int AvailableHistory => Math.Max(0, HistoryBudget - EstimatedHistory);

    public ContextBudgetSnapshot WithHistoryEstimate(int estimatedHistory, int historyBudget)
    {
        var budget = historyBudget > 0 ? historyBudget : HistoryBudget;
        var utilization = budget > 0 ? (double)estimatedHistory / budget : 1.0;
        return this with
        {
            HistoryBudget = budget,
            EstimatedHistory = estimatedHistory,
            Utilization = utilization
        };
    }
}
