namespace Athlon.Agent.Core.Plan;

public interface IPlanPhaseAccessor
{
    PlanPhase? GetPhase(string? sessionId);

    PlanRun? GetActiveRun(string? sessionId);

    void SetActiveRun(PlanRun? run);

    void Clear(string sessionId);
}
