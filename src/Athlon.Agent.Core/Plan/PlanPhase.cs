namespace Athlon.Agent.Core.Plan;

public enum PlanPhase
{
    Explore = 0,
    Draft = 1,
    AwaitConfirm = 2,
    Done = 3
}

public static class PlanPhaseRules
{
    public static bool IsAwaitingUser(this PlanPhase phase) =>
        phase == PlanPhase.AwaitConfirm;

    public static bool BlocksMcp(this PlanPhase phase) =>
        phase is PlanPhase.AwaitConfirm or PlanPhase.Done;

    public static bool IsReadOnly(this PlanPhase phase) =>
        phase is PlanPhase.Explore or PlanPhase.AwaitConfirm or PlanPhase.Done;

    public static bool AllowsPublishPlan(this PlanPhase phase) =>
        phase == PlanPhase.Draft;
}
