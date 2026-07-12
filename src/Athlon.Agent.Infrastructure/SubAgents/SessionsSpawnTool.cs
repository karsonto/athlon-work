using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SessionsSpawnTool(
    AppSettings settings,
    Lazy<ISubAgentSessionManager> sessionManager,
    IActiveAgentSessionContext activeSessionContext) : IAgentTool, ISubAgentTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        Name: "sessions_spawn",
        Description:
            "Spawn a sub-agent session with a role and optional first message. "
            + "Use label to reuse the same child across turns. "
            + "timeout_seconds=0 runs asynchronously and returns task_id.",
        ParametersSchema: ToolSchema.Object()
            .String("role", "Who the child agent is: responsibilities, boundaries, output style.", required: true, minLength: 1)
            .String("message", "First task message for the child.")
            .String("label", "Stable label; reuses an existing child session when matched.")
            .Integer("timeout_seconds", "Sync wait in seconds (default from settings). 0 = async background task.", minimum: 0, maximum: 3600)
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
            return ToolResult.Failure("No parent session", "sessions_spawn requires an active parent agent session.");
        }

        invocation.Arguments.TryGetString("role", out var role);
        invocation.Arguments.TryGetString("message", out var message);
        invocation.Arguments.TryGetString("label", out var label);
        var timeout = ParseTimeout(invocation.Arguments);

        var result = await sessionManager.Value.SpawnAsync(
            parentSessionId,
            role?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
            string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            timeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsOk && !string.Equals(result.Status, "accepted", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Failure("sessions_spawn failed", result.Error ?? result.Status);
        }

        var content = SubAgentResultFormatter.FormatSpawnInfo(result, result.ReusedExisting);
        return ToolResult.Success("sessions_spawn completed", content);
    }

    private static int? ParseTimeout(ToolCallArguments arguments)
    {
        return arguments.TryGetInt32("timeout_seconds", out var value) ? value : null;
    }
}
