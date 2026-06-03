namespace Athlon.Agent.Core.Compaction;

public static class ContextPressureEvaluator
{
    /// <summary>Utilization at which truncateArgs / prefix re-evict may begin (budget-adjusted).</summary>
    public static double ResolveTruncateThreshold(DynamicCompactionSettings settings) =>
        settings.TargetUtilization * settings.TruncateLeadRatio;

    /// <summary>Utilization at which conversation compact may begin (budget-adjusted).</summary>
    public static double ResolveCompactThreshold(DynamicCompactionSettings settings) =>
        settings.TargetUtilization;

    public static ContextPressureLevel Evaluate(
        ContextBudgetSnapshot budget,
        DynamicCompactionSettings settings,
        bool forceOverflow = false)
    {
        if (forceOverflow)
        {
            return ContextPressureLevel.Overflow;
        }

        var utilization = budget.TotalUtilization;
        var target = settings.TargetUtilization;

        if (utilization >= target)
        {
            return ContextPressureLevel.Critical;
        }

        if (utilization >= ResolveTruncateThreshold(settings))
        {
            return ContextPressureLevel.High;
        }

        if (utilization >= target * 0.6875)
        {
            return ContextPressureLevel.Elevated;
        }

        return ContextPressureLevel.Normal;
    }

    /// <summary>
    /// History keep budget after compaction. When a full 3-level pass includes LLM compact (or overflow),
    /// targets <see cref="DynamicCompactionSettings.PostCompactionUtilization"/> (~30% window).
    /// Truncate/re-evict-only passes fall back to the static keep floor.
    /// </summary>
    public static int ResolveKeepTokenBudget(
        ContextBudgetSnapshot budget,
        ContextPressureLevel pressure,
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        bool includesConversationCompact)
    {
        var dynamic = settings.DynamicCompaction;
        var staticKeep = ResolveStaticKeepTokenBudget(conversation, settings);

        if (!dynamic.Enabled)
        {
            return staticKeep;
        }

        if (pressure != ContextPressureLevel.Overflow && !includesConversationCompact)
        {
            return Math.Max(staticKeep, 512);
        }

        var keepTargetUtil = pressure == ContextPressureLevel.Overflow
            ? dynamic.OverflowPostCompactionUtilization
            : dynamic.PostCompactionUtilization;
        var targetHistory = (int)Math.Floor(keepTargetUtil * budget.UsablePromptWindow - budget.FixedOverhead);
        var dynamicKeep = Math.Max(512, targetHistory);

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

        return budget.TotalUtilization >= ResolveTruncateThreshold(settings.DynamicCompaction);
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
            && budget.TotalUtilization >= ResolveTruncateThreshold(settings.DynamicCompaction);
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

        return budget.TotalUtilization >= ResolveCompactThreshold(settings.DynamicCompaction);
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
