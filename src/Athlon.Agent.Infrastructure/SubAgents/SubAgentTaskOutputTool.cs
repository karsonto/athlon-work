using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SubAgentTaskOutputTool(
    AppSettings settings,
    Lazy<ISubAgentSessionManager> sessionManager,
    IActiveAgentSessionContext activeSessionContext) : IAgentTool, ISubAgentTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        Name: "task_output",
        Description: "Retrieve the result of an async sub-agent task (status accepted + task_id).",
        ParametersSchema: ToolSchema.Object()
            .String("task_id", "Task id from sessions_spawn or sessions_send.", required: true)
            .Build(),
        Group: ToolGroup.SubAgent);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!settings.SubAgent.Enabled)
        {
            return ToolResult.Failure("Sub-agent disabled", "Sub-agent tools are disabled in settings.");
        }

        var parentSessionId = activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(parentSessionId))
        {
            return ToolResult.Failure("No parent session", "task_output requires an active parent session.");
        }

        if (!invocation.Arguments.TryGetValue("task_id", out var taskId) || string.IsNullOrWhiteSpace(taskId))
        {
            return ToolResult.Failure("Missing task_id", "Required parameter: task_id");
        }

        var record = await sessionManager.Value.GetTaskOutputAsync(parentSessionId, taskId.Trim(), cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return ToolResult.Failure("Unknown task_id", $"No task '{taskId}' for this session.");
        }

        if (string.Equals(record.Status, "pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(record.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Success("Task still running", $"status: {record.Status}\ntask_id: {record.TaskId}");
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("status: ").AppendLine(record.Status);
        sb.Append("task_id: ").AppendLine(record.TaskId);
        sb.Append("session_key: ").AppendLine(record.SessionKey);
        if (!string.IsNullOrWhiteSpace(record.Error))
        {
            sb.Append("error: ").AppendLine(record.Error);
        }

        if (!string.IsNullOrWhiteSpace(record.Result))
        {
            sb.AppendLine("---");
            sb.AppendLine(record.Result);
        }

        return ToolResult.Success("task_output", sb.ToString().TrimEnd());
    }
}
