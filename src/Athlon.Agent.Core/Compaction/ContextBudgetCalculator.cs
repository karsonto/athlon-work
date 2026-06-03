namespace Athlon.Agent.Core.Compaction;

public static class ContextBudgetCalculator
{
    public static ContextBudgetSnapshot Compute(
        string environmentPrompt,
        IReadOnlyList<ToolDefinition> tools,
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings compactionSettings,
        ModelSettings modelSettings,
        double calibrationMultiplier = 1.0)
    {
        var dynamic = compactionSettings.DynamicCompaction;
        var totalWindow = Math.Max(1, compactionSettings.ContextWindowTokens);
        var reservedOutput = modelSettings.MaxTokens is > 0
            ? modelSettings.MaxTokens.Value
            : dynamic.DefaultReservedOutputTokens;

        var systemTokens = ContextTokenEstimator.EstimateTextTokens(environmentPrompt, calibrationMultiplier);
        var toolsTokens = EstimateToolsTokens(tools, calibrationMultiplier);
        var margin = (int)Math.Floor(totalWindow * dynamic.SafetyMarginRatio);
        var fixedOverhead = systemTokens + toolsTokens + margin;
        var historyBudget = Math.Max(512, totalWindow - reservedOutput - fixedOverhead);

        var conversation = FilterConversation(messages);
        var estimatedHistory = ContextTokenEstimator.Estimate(
            conversation,
            compactionSettings.IncludeReasoningInModelContext,
            calibrationMultiplier);
        var utilization = historyBudget > 0 ? (double)estimatedHistory / historyBudget : 1.0;

        return new ContextBudgetSnapshot(
            totalWindow,
            reservedOutput,
            fixedOverhead,
            historyBudget,
            estimatedHistory,
            utilization);
    }

    public static ContextBudgetSnapshot RecomputeHistory(
        ContextBudgetSnapshot snapshot,
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings compactionSettings,
        double calibrationMultiplier = 1.0)
    {
        var conversation = FilterConversation(messages);
        var estimatedHistory = ContextTokenEstimator.Estimate(
            conversation,
            compactionSettings.IncludeReasoningInModelContext,
            calibrationMultiplier);

        return snapshot.WithHistoryEstimate(estimatedHistory, snapshot.HistoryBudget);
    }

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
            foreach (var parameter in tool.Parameters)
            {
                total += ContextTokenEstimator.EstimateTextTokens(parameter.Key, calibrationMultiplier);
                total += ContextTokenEstimator.EstimateTextTokens(parameter.Value, calibrationMultiplier);
            }
        }

        return total + ContextTokenEstimator.EstimateTextTokens("schema-overhead", calibrationMultiplier);
    }

    private static List<ChatMessage> FilterConversation(IReadOnlyList<ChatMessage> messages) =>
        messages.Where(message => message.Role != MessageRole.Compaction).ToList();
}
