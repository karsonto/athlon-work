namespace Athlon.Agent.Core.Compaction;

public static class ContextPressureEvaluator
{
    public static ContextPressureLevel Evaluate(
        ContextBudgetSnapshot budget,
        DynamicCompactionSettings settings,
        bool forceOverflow = false)
    {
        if (forceOverflow)
        {
            return ContextPressureLevel.Overflow;
        }

        var totalUtilization = budget.TotalUtilization;

        if (totalUtilization >= settings.CriticalUtilization)
        {
            return ContextPressureLevel.Critical;
        }

        if (totalUtilization >= settings.HighUtilization)
        {
            return ContextPressureLevel.High;
        }

        if (totalUtilization >= settings.ElevatedUtilization)
        {
            return ContextPressureLevel.Elevated;
        }

        return ContextPressureLevel.Normal;
    }

    public static double ResolveKeepRatio(ContextPressureLevel pressure, DynamicCompactionSettings settings) =>
        pressure switch
        {
            ContextPressureLevel.Overflow => settings.KeepRatioOverflow,
            ContextPressureLevel.Critical => settings.KeepRatioCritical,
            ContextPressureLevel.High => settings.KeepRatioElevated,
            ContextPressureLevel.Elevated => settings.KeepRatioElevated,
            _ => settings.KeepRatioElevated
        };

    public static int ResolveKeepTokenBudget(
        ContextBudgetSnapshot budget,
        ContextPressureLevel pressure,
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings)
    {
        var dynamicKeep = Math.Max(
            512,
            (int)Math.Floor(budget.HistoryBudget * ResolveKeepRatio(pressure, settings.DynamicCompaction)));
        var staticKeep = ResolveStaticKeepTokenBudget(conversation, settings);
        return Math.Max(dynamicKeep, staticKeep);
    }

    public static bool ShouldApplyTruncateArgs(
        ContextBudgetSnapshot budget,
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        ContextPressureLevel pressure,
        bool force)
    {
        if (force || pressure is ContextPressureLevel.Overflow)
        {
            return true;
        }

        if (MeetsStaticTruncateThreshold(conversation, settings))
        {
            return true;
        }

        return budget.TotalUtilization >= settings.DynamicCompaction.HighUtilization;
    }

    public static bool ShouldApplyPrefixReEvict(
        ContextBudgetSnapshot budget,
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        ContextPressureLevel pressure,
        bool force)
    {
        if (force || pressure is ContextPressureLevel.Overflow or ContextPressureLevel.Critical)
        {
            return ShouldApplyTruncateArgs(budget, conversation, settings, pressure, force);
        }

        return ShouldApplyTruncateArgs(budget, conversation, settings, pressure, force)
            && budget.TotalUtilization >= settings.DynamicCompaction.HighUtilization;
    }

    public static bool ShouldCompact(
        ContextBudgetSnapshot budget,
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        ContextPressureLevel pressure,
        bool force)
    {
        if (force || pressure is ContextPressureLevel.Overflow)
        {
            return true;
        }

        if (!settings.DynamicCompaction.Enabled)
        {
            var estimated = ContextTokenEstimator.Estimate(conversation, settings.IncludeReasoningInModelContext);
            return ConversationCutoffPlanner.ShouldCompact(conversation, estimated, settings, force: false);
        }

        if (MeetsStaticCompactThreshold(conversation, settings))
        {
            return true;
        }

        return budget.TotalUtilization >= settings.DynamicCompaction.CriticalUtilization;
    }

    public static bool MeetsStaticTruncateThreshold(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings)
    {
        var estimated = ContextTokenEstimator.Estimate(conversation, settings.IncludeReasoningInModelContext);
        return ConversationCutoffPlanner.ShouldTruncateArgs(
            conversation,
            estimated,
            settings.TruncateArgs);
    }

    public static bool MeetsStaticCompactThreshold(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings)
    {
        var estimated = ContextTokenEstimator.Estimate(conversation, settings.IncludeReasoningInModelContext);
        return ConversationCutoffPlanner.ShouldCompact(conversation, estimated, settings, force: false);
    }

    private static int ResolveStaticKeepTokenBudget(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings)
    {
        if (settings.KeepTokens > 0)
        {
            return settings.KeepTokens;
        }

        if (settings.KeepMessages <= 0 || conversation.Count == 0)
        {
            return 0;
        }

        var tailStart = Math.Max(0, conversation.Count - settings.KeepMessages);
        return ContextTokenEstimator.EstimateSuffix(
            conversation,
            tailStart,
            settings.IncludeReasoningInModelContext);
    }
}
