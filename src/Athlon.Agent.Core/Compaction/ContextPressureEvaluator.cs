namespace Athlon.Agent.Core.Compaction;

public static class ContextPressureEvaluator
{
    /// <summary>Utilization at which conversation checkpoint may begin.</summary>
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

        // Display bands only (no pre-checkpoint prune actions).
        if (utilization >= target * 0.90)
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
    /// True when fixed prompt overhead alone consumes so much of the usable window that history
    /// compaction cannot meaningfully free space.
    /// </summary>
    public static bool IsFixedOverheadDominating(
        ContextBudgetSnapshot budget,
        ContextCompactionSettings settings)
    {
        if (budget.UsablePromptWindow <= 0)
        {
            return true;
        }

        var ratio = settings.FixedOverheadDominatesRatio;
        if (ratio <= 0)
        {
            ratio = 0.85;
        }

        return budget.FixedOverhead >= budget.UsablePromptWindow * ratio;
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
            var estimated = ContextTokenEstimator.Estimate(
                conversation,
                settings.IncludeReasoningInModelContext,
                maxToolScreenshots: settings.MaxToolScreenshotsInModelContext,
                hygiene: settings.RequestHistoryHygiene);
            return ConversationCutoffPlanner.ShouldCompact(conversation, estimated, settings, force: false);
        }

        return budget.TotalUtilization >= ResolveCompactThreshold(settings.DynamicCompaction);
    }

    public static bool MeetsStaticCompactThreshold(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        int? knownRawHistoryEstimate = null)
    {
        var estimated = knownRawHistoryEstimate
            ?? ContextTokenEstimator.Estimate(
                conversation,
                settings.IncludeReasoningInModelContext,
                maxToolScreenshots: settings.MaxToolScreenshotsInModelContext,
                hygiene: settings.RequestHistoryHygiene);
        return ConversationCutoffPlanner.ShouldCompact(conversation, estimated, settings, force: false);
    }
}
