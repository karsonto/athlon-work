using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SessionsSendTool(
    AppSettings settings,
    Lazy<ISubAgentSessionManager> sessionManager,
    IActiveAgentSessionContext activeSessionContext) : IAgentTool, ISubAgentTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        Name: "sessions_send",
        Description:
            "Send a message to an existing sub-agent session. "
            + "Provide session_key or label. timeout_seconds=0 runs asynchronously.",
        Parameters: new Dictionary<string, string>
        {
            ["session_key"] = "Stable key from sessions_spawn (sub:parent:child).",
            ["label"] = "Alternative lookup when you used a label at spawn time.",
            ["message"] = "Required task message for this turn.",
            ["timeout_seconds"] = "Sync wait in seconds. 0 = async background task."
        },
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
            return ToolResult.Failure("No parent session", "sessions_send requires an active parent agent session.");
        }

        invocation.Arguments.TryGetValue("session_key", out var sessionKey);
        invocation.Arguments.TryGetValue("label", out var label);
        invocation.Arguments.TryGetValue("message", out var message);
        var timeout = ParseTimeout(invocation.Arguments);

        if (string.IsNullOrWhiteSpace(sessionKey) && string.IsNullOrWhiteSpace(label))
        {
            return ToolResult.Failure("Missing target", "Provide session_key or label.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return ToolResult.Failure("Missing message", "Required parameter: message");
        }

        var result = await sessionManager.Value.SendAsync(
            parentSessionId,
            string.IsNullOrWhiteSpace(sessionKey) ? null : sessionKey.Trim(),
            string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            message.Trim(),
            timeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsOk)
        {
            return ToolResult.Failure("sessions_send failed", result.Error ?? result.Status);
        }

        var content = SubAgentResultFormatter.FormatSendResult(result);
        return ToolResult.Success("sessions_send completed", content);
    }

    private static int? ParseTimeout(IReadOnlyDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue("timeout_seconds", out var text) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text.Trim(), out var value) ? value : null;
    }
}
