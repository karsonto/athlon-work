namespace Athlon.Agent.Core.Plan;

public static class PlanProgress
{
    public static bool HasInProgressSubtask(AgentPlan? plan) =>
        plan?.Subtasks.Any(subtask => subtask.State == SubTaskState.InProgress) == true;
}
