namespace Athlon.Agent.Core.Plan;

public static class PlanAutoContinuePolicy
{
    public static bool ShouldScheduleContinue(
        bool autoContinueEnabled,
        int completedAutoContinueRounds,
        int maxRounds,
        bool cancelled,
        bool timedOut,
        Exception? error,
        AgentPlan? plan)
    {
        if (!autoContinueEnabled)
        {
            return false;
        }

        if (error is not null)
        {
            return false;
        }

        if (cancelled && !timedOut)
        {
            return false;
        }

        if (completedAutoContinueRounds >= maxRounds)
        {
            return false;
        }

        return PlanProgress.HasInProgressSubtask(plan);
    }
}
