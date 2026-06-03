namespace Athlon.Agent.Core.Plan;

public interface IPlanNotebook
{
    AgentPlan? GetCurrent(string sessionId);

    PlanOperationResult CreatePlan(string sessionId, CreatePlanRequest request);

    PlanOperationResult ApprovePlan(string sessionId);

    PlanOperationResult FinishSubtask(string sessionId, int subtaskIndex, string outcome);

    string GetPlanMarkdown(string sessionId, bool detailed = true);

    /// <summary>Returns the on-disk plan file path when a workspace is configured; otherwise null.</summary>
    string? TryGetPlanFilePath();

    void Clear(string sessionId);
}
