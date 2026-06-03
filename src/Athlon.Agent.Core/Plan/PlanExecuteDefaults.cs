namespace Athlon.Agent.Core.Plan;

public static class PlanExecuteDefaults
{
    public const string ExecuteUserMessage =
        """
        Execute the approved plan. Call get_plan first. Work through subtasks in order; call finish_subtask with concrete measurable outcomes when each step is done.
        """;
}
