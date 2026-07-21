namespace Athlon.Agent.Core.Compaction;

public static class ContextBudgetCalculator
{
    public static ContextBudgetSnapshot Compute(
        string environmentPrompt,
        IReadOnlyList<ToolDefinition> tools,
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings compactionSettings,
        ModelSettings modelSettings,
        double calibrationMultiplier = 1.0,
        string? runtimeContext = null)
    {
        var dynamic = compactionSettings.DynamicCompaction;
        var totalWindow = Math.Max(1, compactionSettings.ContextWindowTokens);
        var reservedOutput = modelSettings.MaxTokens is > 0
            ? modelSettings.MaxTokens.Value
            : dynamic.DefaultReservedOutputTokens;

        var systemTokens = ContextTokenEstimator.EstimateTextTokens(environmentPrompt, calibrationMultiplier)
            + ContextTokenEstimator.EstimateTextTokens(runtimeContext ?? string.Empty, calibrationMultiplier);
        var toolsTokens = EstimateToolsTokens(tools, calibrationMultiplier);
        var margin = (int)Math.Floor(totalWindow * dynamic.SafetyMarginRatio);
        var fixedOverhead = systemTokens + toolsTokens + margin;
        var historyBudget = Math.Max(0, totalWindow - reservedOutput - fixedOverhead);

        var conversation = ConversationMessageFilters.WithoutCompactionAudits(messages);
        var estimatedHistory = ContextTokenEstimator.Estimate(
            conversation,
            compactionSettings.IncludeReasoningInModelContext,
            calibrationMultiplier);
        var historyUtilization = historyBudget > 0 ? (double)estimatedHistory / historyBudget : 1.0;

        return new ContextBudgetSnapshot(
            totalWindow,
            reservedOutput,
            fixedOverhead,
            historyBudget,
            estimatedHistory,
            historyUtilization);
    }

    public static ContextBudgetSnapshot RecomputeHistory(
        ContextBudgetSnapshot snapshot,
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings compactionSettings,
        double calibrationMultiplier = 1.0)
    {
        var conversation = ConversationMessageFilters.WithoutCompactionAudits(messages);
        var estimatedHistory = ContextTokenEstimator.Estimate(
            conversation,
            compactionSettings.IncludeReasoningInModelContext,
            calibrationMultiplier);

        return snapshot.WithHistoryEstimate(estimatedHistory, snapshot.HistoryBudget);
    }

    /// <summary>Uncalibrated history estimate for static thresholds / ResolveEffectiveEstimate reuse.</summary>
    public static int EstimateRawHistory(
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings compactionSettings) =>
        ContextTokenEstimator.Estimate(
            ConversationMessageFilters.WithoutCompactionAudits(messages),
            compactionSettings.IncludeReasoningInModelContext);

    private static int EstimateToolsTokens(IReadOnlyList<ToolDefinition> tools, double calibrationMultiplier)
    {
        if (tools.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var tool in tools)
        {
            total += ContextTokenEstimator.EstimateTextTokens(tool.Name, calibrationMultiplier);
            total += ContextTokenEstimator.EstimateTextTokens(tool.Description, calibrationMultiplier);
            total += ContextTokenEstimator.EstimateTextTokens(tool.Source, calibrationMultiplier);
            total += ContextTokenEstimator.EstimateTextTokens(tool.ParametersSchema.ToCanonicalJson(), calibrationMultiplier);
        }

        return total + ContextTokenEstimator.EstimateTextTokens("schema-overhead", calibrationMultiplier);
    }
}
