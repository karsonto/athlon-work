namespace Athlon.Agent.Core.Compaction;

public sealed record DynamicCompactionPlan(
    ContextPressureLevel Pressure,
    bool ApplyTruncateArgs,
    bool ApplyPrefixReEvict,
    bool ApplyConversationCompact,
    int KeepTokenBudget,
    string? MustPreserveAppendix)
{
    public static DynamicCompactionPlan Create(
        ContextPressureLevel pressure,
        ContextBudgetSnapshot budget,
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        bool force,
        int? knownRawHistoryEstimate = null)
    {
        var dynamic = settings.DynamicCompaction;
        if (!dynamic.Enabled)
        {
            return new DynamicCompactionPlan(
                pressure,
                ApplyTruncateArgs: false,
                ApplyPrefixReEvict: false,
                ApplyConversationCompact: ContextPressureEvaluator.ShouldCompact(
                    budget,
                    conversation,
                    settings,
                    pressure,
                    force),
                KeepTokenBudget: 0,
                MustPreserveAppendix: null);
        }

        var applyTruncate = ContextPressureEvaluator.ShouldApplyTruncateArgs(
            budget,
            conversation,
            settings,
            pressure,
            force,
            knownRawHistoryEstimate);
        var applyReEvict = ContextPressureEvaluator.ShouldApplyPrefixReEvict(
            budget,
            conversation,
            settings,
            pressure,
            force,
            applyTruncate,
            knownRawHistoryEstimate);
        var applyCompact = ContextPressureEvaluator.ShouldCompact(
            budget,
            conversation,
            settings,
            pressure,
            force);

        var keepTokenBudget = ContextPressureEvaluator.ResolveKeepTokenBudget(
            budget,
            pressure,
            conversation,
            settings,
            applyCompact || force);

        string? mustPreserve = null;
        if (dynamic.EnableSemanticCutoff && applyCompact)
        {
            mustPreserve = SemanticCutoffPlanner.BuildMustPreserveAppendix(conversation, settings, keepTokenBudget);
        }

        return new DynamicCompactionPlan(
            pressure,
            applyTruncate,
            applyReEvict,
            applyCompact,
            keepTokenBudget,
            mustPreserve);
    }
}
