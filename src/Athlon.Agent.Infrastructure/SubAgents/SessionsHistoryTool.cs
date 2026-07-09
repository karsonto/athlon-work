using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SessionsHistoryTool(
    AppSettings settings,
    Lazy<ISubAgentSessionManager> sessionManager,
    IActiveAgentSessionContext activeSessionContext) : IAgentTool, ISubAgentTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        Name: "sessions_history",
        Description: "Read recent transcript lines from a sub-agent session.",
        ParametersSchema: ToolSchema.Object()
            .String("session_key", "Session key from sessions_spawn or sessions_list.", required: true)
            .Integer("limit", "Max messages to return (default 20).")
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
            return ToolResult.Failure("No parent session", "sessions_history requires an active parent agent session.");
        }

        if (!invocation.Arguments.TryGetValue("session_key", out var sessionKey) || string.IsNullOrWhiteSpace(sessionKey))
        {
            return ToolResult.Failure("Missing session_key", "Required parameter: session_key");
        }

        var limit = 20;
        if (invocation.Arguments.TryGetValue("limit", out var limitText)
            && int.TryParse(limitText.Trim(), out var parsed))
        {
            limit = parsed;
        }

        var result = await sessionManager.Value.HistoryAsync(
            parentSessionId,
            sessionKey.Trim(),
            limit,
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return ToolResult.Failure("sessions_history failed", result.Error);
        }

        return ToolResult.Success("sessions_history", result.Content ?? "(empty)");
    }
}
