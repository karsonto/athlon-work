using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Infrastructure.Plan;

public sealed class FinishSubtaskTool(IPlanNotebook planNotebook, IActiveAgentSessionContext sessionContext) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "finish_subtask",
        "Mark a subtask as done with a specific, measurable outcome. Finish subtasks in order.",
        new Dictionary<string, string>
        {
            ["subtask_idx"] = "Zero-based index of the subtask to finish",
            ["subtask_outcome"] =
                "Specific outcome achieved (data, paths, counts) — not a narrative of what you did"
        });

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = PlanToolBase.RequireSessionId(sessionContext);
        if (sessionId is null)
        {
            return Task.FromResult(PlanToolBase.MissingSession());
        }

        if (!ToolArguments.TryGetRequired(invocation, "subtask_outcome", out var outcome, out var error))
        {
            return Task.FromResult(error);
        }

        var subtaskIndex = ToolArguments.GetInt32(invocation, "subtask_idx", -1);
        if (subtaskIndex < 0)
        {
            return Task.FromResult(ToolResult.Failure("Missing argument", "finish_subtask requires `subtask_idx`."));
        }

        var result = planNotebook.FinishSubtask(sessionId, subtaskIndex, outcome);
        return Task.FromResult(
            result.Success
                ? ToolResult.Success(result.Message, result.Message)
                : ToolResult.Failure("Finish subtask failed", result.Message));
    }
}
