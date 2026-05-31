namespace Athlon.Agent.Core.Plan;

public interface IPlanNotebook
{
    AgentPlan? GetCurrent(string sessionId);

    PlanOperationResult CreatePlan(string sessionId, CreatePlanRequest request);

    PlanOperationResult FinishSubtask(string sessionId, int subtaskIndex, string outcome);

    string GetPlanMarkdown(string sessionId, bool detailed = true);
}
