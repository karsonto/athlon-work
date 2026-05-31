using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Infrastructure.Plan;

public sealed class CreatePlanTool(IPlanNotebook planNotebook, IActiveAgentSessionContext sessionContext) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "create_plan",
        "Create a plan with ordered sub-tasks for multi-step work. Replaces any existing session plan.",
        new Dictionary<string, string>
        {
            ["name"] = "Concise plan name (about 10 words or fewer)",
            ["description"] = "Plan description: constraints, target, measurable outcome",
            ["expected_outcome"] = "Expected outcome of the full plan",
            ["subtasks"] =
                "JSON array of subtask objects with name (required), description, expected_outcome. "
                + "Example: [{\"name\":\"Step 1\",\"description\":\"...\",\"expected_outcome\":\"...\"}]"
        });

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = PlanToolBase.RequireSessionId(sessionContext);
        if (sessionId is null)
        {
            return Task.FromResult(PlanToolBase.MissingSession());
        }

        if (!ToolArguments.TryGetRequired(invocation, "name", out var name, out var error))
        {
            return Task.FromResult(error);
        }

        if (!ToolArguments.TryGetRequired(invocation, "description", out var description, out error))
        {
            return Task.FromResult(error);
        }

        if (!ToolArguments.TryGetRequired(invocation, "expected_outcome", out var expectedOutcome, out error))
        {
            return Task.FromResult(error);
        }

        if (!ToolArguments.TryGetRequired(invocation, "subtasks", out var subtasksJson, out error))
        {
            return Task.FromResult(error);
        }

        if (!SubTaskJsonParser.TryParse(subtasksJson, out var subtasks, out var parseError))
        {
            return Task.FromResult(ToolResult.Failure("Invalid subtasks", parseError));
        }

        var result = planNotebook.CreatePlan(
            sessionId,
            new CreatePlanRequest(name, description, expectedOutcome, subtasks));

        return Task.FromResult(
            result.Success
                ? ToolResult.Success(result.Message, result.Message)
                : ToolResult.Failure("Create plan failed", result.Message));
    }
}
