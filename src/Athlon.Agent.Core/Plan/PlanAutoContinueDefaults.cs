namespace Athlon.Agent.Core.Plan;

public static class PlanAutoContinueDefaults
{
    public const string ContinueUserMessage =
        """
        Auto-continue: an in-progress subtask remains on the plan. Call get_plan first. Continue only that subtask; call finish_subtask with a concrete outcome when it is done. Do not claim the overall task is complete until every subtask is done or abandoned. If the current subtask is too large for one turn, split remaining work into smaller subtasks via create_plan before proceeding.
        """;
}
