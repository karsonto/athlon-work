namespace Athlon.Agent.Core.Compaction;

public sealed record ContextBudgetSnapshot(
    int TotalWindow,
    int ReservedOutput,
    int FixedOverhead,
    int HistoryBudget,
    int EstimatedHistory,
    /// <summary>estimatedHistory / historyBudget — informational only; not used for pressure triggers.</summary>
    double HistoryUtilization,
    int SystemTokens = 0,
    int ToolsTokens = 0,
    int MarginTokens = 0,
    ContextOccupancyBreakdown Occupancy = null!)
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

    public bool HasOccupancy => TotalWindow > 1 && UsablePromptWindow > 0;

    public ContextOccupancyBreakdown DisplayOccupancy
    {
        get
        {
            if (Occupancy is { ContentTokens: > 0 })
            {
                return Occupancy;
            }

            return new ContextOccupancyBreakdown(
                SystemPrompt: SystemTokens,
                ToolDefinitions: ToolsTokens,
                Conversation: EstimatedHistory);
        }
    }

    public int DisplayedContentTokens
    {
        get
        {
            var occupancy = DisplayOccupancy;
            return occupancy.ContentTokens > 0
                ? occupancy.ContentTokens
                : Math.Max(0, EstimatedTotalPrompt - MarginTokens);
        }
    }

    public ContextBudgetSnapshot WithHistoryEstimate(int estimatedHistory, int historyBudget)
    {
        var budget = historyBudget > 0 ? historyBudget : HistoryBudget;
        var historyUtilization = budget > 0 ? (double)estimatedHistory / budget : 1.0;
        var occupancy = DisplayOccupancy with { Conversation = estimatedHistory };
        return this with
        {
            HistoryBudget = budget,
            EstimatedHistory = estimatedHistory,
            HistoryUtilization = historyUtilization,
            Occupancy = occupancy
        };
    }
}
