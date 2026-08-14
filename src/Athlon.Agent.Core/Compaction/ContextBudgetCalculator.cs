using Athlon.Agent.Core.Prompt;

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
        string? runtimeContext = null,
        PromptOccupancyTokens? promptOccupancy = null)
    {
        var dynamic = compactionSettings.DynamicCompaction;
        var totalWindow = Math.Max(1, compactionSettings.ContextWindowTokens);
        var reservedOutput = modelSettings.MaxTokens is > 0
            ? modelSettings.MaxTokens.Value
            : dynamic.DefaultReservedOutputTokens;

        var systemTokens = ContextTokenEstimator.EstimateTextTokens(environmentPrompt, calibrationMultiplier)
            + ContextTokenEstimator.EstimateTextTokens(runtimeContext ?? string.Empty, calibrationMultiplier);
        var toolBuckets = EstimateToolBuckets(tools, calibrationMultiplier);
        var toolsTokens = toolBuckets.Builtin + toolBuckets.Mcp + toolBuckets.Subagent;
        var margin = (int)Math.Floor(totalWindow * dynamic.SafetyMarginRatio);
        var fixedOverhead = systemTokens + toolsTokens + margin;
        var historyBudget = Math.Max(0, totalWindow - reservedOutput - fixedOverhead);

        var conversation = ConversationMessageFilters.WithoutCompactionAudits(messages);
        var estimatedHistory = ContextTokenEstimator.Estimate(
            conversation,
            compactionSettings.IncludeReasoningInModelContext,
            calibrationMultiplier,
            compactionSettings.MaxToolScreenshotsInModelContext);
        var historyUtilization = historyBudget > 0 ? (double)estimatedHistory / historyBudget : 1.0;
        var occupancy = BuildOccupancy(
            systemTokens,
            toolsTokens,
            estimatedHistory,
            runtimeContext,
            calibrationMultiplier,
            promptOccupancy,
            toolBuckets);

        return new ContextBudgetSnapshot(
            totalWindow,
            reservedOutput,
            fixedOverhead,
            historyBudget,
            estimatedHistory,
            historyUtilization,
            systemTokens,
            toolsTokens,
            margin,
            occupancy);
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
            calibrationMultiplier,
            compactionSettings.MaxToolScreenshotsInModelContext);

        return snapshot.WithHistoryEstimate(estimatedHistory, snapshot.HistoryBudget);
    }

    /// <summary>Uncalibrated history estimate for static thresholds / ResolveEffectiveEstimate reuse.</summary>
    public static int EstimateRawHistory(
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings compactionSettings) =>
        ContextTokenEstimator.Estimate(
            ConversationMessageFilters.WithoutCompactionAudits(messages),
            compactionSettings.IncludeReasoningInModelContext,
            maxToolScreenshots: compactionSettings.MaxToolScreenshotsInModelContext);

    private static ContextOccupancyBreakdown BuildOccupancy(
        int systemTokens,
        int toolsTokens,
        int estimatedHistory,
        string? runtimeContext,
        double calibrationMultiplier,
        PromptOccupancyTokens? promptOccupancy,
        ToolTokenBuckets toolBuckets)
    {
        var runtimeTokens = ContextTokenEstimator.EstimateTextTokens(
            runtimeContext ?? string.Empty,
            calibrationMultiplier);
        if (promptOccupancy is null || promptOccupancy.Total <= 0)
        {
            return new ContextOccupancyBreakdown(
                SystemPrompt: Math.Max(0, systemTokens),
                ToolDefinitions: toolBuckets.Builtin > 0 ? toolBuckets.Builtin : toolsTokens,
                McpTools: toolBuckets.Mcp,
                SubagentDefinitions: toolBuckets.Subagent,
                Conversation: estimatedHistory);
        }

        var scaled = ScalePromptOccupancy(promptOccupancy, Math.Max(0, systemTokens - runtimeTokens));
        return new ContextOccupancyBreakdown(
            SystemPrompt: scaled.SystemPrompt + runtimeTokens,
            ToolDefinitions: toolBuckets.Builtin,
            Rules: scaled.Rules,
            Skills: scaled.Skills,
            McpTools: toolBuckets.Mcp,
            SubagentDefinitions: scaled.Subagent + toolBuckets.Subagent,
            Conversation: estimatedHistory);
    }

    private static PromptOccupancyTokens ScalePromptOccupancy(PromptOccupancyTokens occupancy, int targetTotal)
    {
        if (occupancy.Total <= 0 || targetTotal <= 0)
        {
            return occupancy with { SystemPrompt = Math.Max(occupancy.SystemPrompt, targetTotal) };
        }

        if (occupancy.Total == targetTotal)
        {
            return occupancy;
        }

        var factor = (double)targetTotal / occupancy.Total;
        var system = (int)Math.Round(occupancy.SystemPrompt * factor);
        var rules = (int)Math.Round(occupancy.Rules * factor);
        var skills = (int)Math.Round(occupancy.Skills * factor);
        var subagent = (int)Math.Round(occupancy.Subagent * factor);
        var delta = targetTotal - (system + rules + skills + subagent);
        return new PromptOccupancyTokens(
            system + delta,
            rules,
            skills,
            subagent);
    }

    private static ToolTokenBuckets EstimateToolBuckets(
        IReadOnlyList<ToolDefinition> tools,
        double calibrationMultiplier)
    {
        if (tools.Count == 0)
        {
            return default;
        }

        var builtin = 0;
        var mcp = 0;
        var subagent = 0;
        foreach (var tool in tools)
        {
            var tokens = EstimateToolTokens(tool, calibrationMultiplier);
            if (tool.Group == ToolGroup.SubAgent)
            {
                subagent += tokens;
            }
            else if (tool.Group == ToolGroup.Mcp || IsMcpSource(tool.Source))
            {
                mcp += tokens;
            }
            else
            {
                builtin += tokens;
            }
        }

        var overhead = ContextTokenEstimator.EstimateTextTokens("schema-overhead", calibrationMultiplier);
        if (builtin > 0)
        {
            builtin += overhead;
        }
        else if (mcp > 0)
        {
            mcp += overhead;
        }
        else
        {
            subagent += overhead;
        }

        return new ToolTokenBuckets(builtin, mcp, subagent);
    }

    private static bool IsMcpSource(string? source) =>
        !string.IsNullOrWhiteSpace(source)
        && source.StartsWith("mcp", StringComparison.OrdinalIgnoreCase);

    private static int EstimateToolTokens(ToolDefinition tool, double calibrationMultiplier) =>
        ContextTokenEstimator.EstimateTextTokens(tool.Name, calibrationMultiplier)
        + ContextTokenEstimator.EstimateTextTokens(tool.Description, calibrationMultiplier)
        + ContextTokenEstimator.EstimateTextTokens(tool.Source, calibrationMultiplier)
        + ContextTokenEstimator.EstimateTextTokens(tool.ParametersSchema.ToCanonicalJson(), calibrationMultiplier);

    private readonly record struct ToolTokenBuckets(int Builtin, int Mcp, int Subagent);
}
