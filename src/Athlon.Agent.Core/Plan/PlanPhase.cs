namespace Athlon.Agent.Core.Plan;

public enum PlanPhase
{
    Explore = 0,
    Draft = 1,
    AwaitConfirm = 2,
    Done = 3,
    AwaitClarify = 4
}

public static class PlanPhaseRules
{
    public static bool IsAwaitingUser(this PlanPhase phase) =>
        phase is PlanPhase.AwaitConfirm or PlanPhase.AwaitClarify;

    public static bool BlocksMcp(this PlanPhase phase) =>
        phase is PlanPhase.AwaitConfirm or PlanPhase.AwaitClarify or PlanPhase.Done;

    public static bool IsReadOnly(this PlanPhase phase) =>
        phase is PlanPhase.Explore or PlanPhase.AwaitConfirm or PlanPhase.AwaitClarify or PlanPhase.Done;

    public static bool AllowsPublishPlan(this PlanPhase phase) =>
        phase is PlanPhase.Explore or PlanPhase.Draft;

    public static bool AllowsAskClarification(this PlanPhase phase) =>
        phase == PlanPhase.Explore;
}
