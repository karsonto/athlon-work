namespace Athlon.Agent.Core.Compaction;

public sealed record DynamicCompactionPlan(
    ContextPressureLevel Pressure,
    bool ApplyConversationCompact,
    int RetainUserTokenBudget)
{
    public static DynamicCompactionPlan Create(
        ContextPressureLevel pressure,
        ContextBudgetSnapshot budget,
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        bool force)
    {
        var applyCompact = ContextPressureEvaluator.ShouldCompact(
            budget,
            conversation,
            settings,
            pressure,
            force);

        var retainBudget = CheckpointHistoryBuilder.ResolveRetainUserTokenBudget(
            settings,
            pressure,
            force);

        return new DynamicCompactionPlan(pressure, applyCompact, retainBudget);
    }
}
