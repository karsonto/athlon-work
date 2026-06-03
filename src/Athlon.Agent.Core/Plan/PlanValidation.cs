namespace Athlon.Agent.Core.Plan;

public static class PlanValidation
{
    public static PlanOperationResult? ValidateCreateRequest(CreatePlanRequest request, PlanSettings settings)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new PlanOperationResult(false, "Plan name is required.");
        }

        if (request.Subtasks.Count == 0)
        {
            return new PlanOperationResult(false, "At least one subtask is required.");
        }

        if (request.Subtasks.Count > settings.MaxSubtasks)
        {
            return new PlanOperationResult(
                false,
                $"Cannot create plan: {request.Subtasks.Count} subtasks exceeds the maximum of {settings.MaxSubtasks}.");
        }

        var overview = ResolveOverview(request);
        if (overview.Length < settings.MinOverviewChars)
        {
            return new PlanOperationResult(
                false,
                $"overview must be at least {settings.MinOverviewChars} characters (Markdown: context, goals, key decisions).");
        }

        for (var index = 0; index < request.Subtasks.Count; index++)
        {
            var subtask = request.Subtasks[index];
            if (string.IsNullOrWhiteSpace(subtask.Name))
            {
                return new PlanOperationResult(false, $"subtasks[{index}] requires a non-empty name.");
            }

            var description = subtask.Description?.Trim() ?? string.Empty;
            if (description.Length < settings.MinSubtaskDescriptionChars)
            {
                return new PlanOperationResult(
                    false,
                    $"subtasks[{index}].description must be at least {settings.MinSubtaskDescriptionChars} characters "
                    + "(concrete changes, paths, types, or commands).");
            }

            var expectedOutcome = subtask.ExpectedOutcome?.Trim() ?? string.Empty;
            if (expectedOutcome.Length < settings.MinSubtaskExpectedOutcomeChars)
            {
                return new PlanOperationResult(
                    false,
                    $"subtasks[{index}].expected_outcome must be at least {settings.MinSubtaskExpectedOutcomeChars} characters "
                    + "(measurable acceptance criteria).");
            }
        }

        return null;
    }

    public static string ResolveOverview(CreatePlanRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Overview))
        {
            return request.Overview.Trim();
        }

        return request.Description?.Trim() ?? string.Empty;
    }

    public static AgentSubTask ToSubtask(SubTaskInput input) =>
        new(
            input.Name.Trim(),
            input.Description?.Trim() ?? string.Empty,
            input.ExpectedOutcome?.Trim() ?? string.Empty,
            input.Files);

    public static AgentPlan ToPlan(CreatePlanRequest request, PlanPhase phase = PlanPhase.Draft) =>
        new(
            request.Name.Trim(),
            request.Description?.Trim() ?? string.Empty,
            request.ExpectedOutcome?.Trim() ?? string.Empty,
            request.Subtasks.Select(ToSubtask).ToList(),
            ResolveOverview(request),
            request.Architecture?.Trim(),
            request.Mermaid?.Trim(),
            request.TestingStrategy?.Trim(),
            request.OutOfScope?.Trim(),
            phase);
}
