using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SessionsListTool(
    AppSettings settings,
    Lazy<ISubAgentSessionManager> sessionManager,
    IActiveAgentSessionContext activeSessionContext) : IAgentTool, ISubAgentTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        Name: "sessions_list",
        Description: "List sub-agent sessions for the current parent session.",
        Parameters: new Dictionary<string, string>(),
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
            return ToolResult.Failure("No parent session", "sessions_list requires an active parent agent session.");
        }

        var entries = await sessionManager.Value.ListAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return ToolResult.Success("No sub-agent sessions", "(none)");
        }

        var lines = entries.Select(entry =>
            string.Join(
                " | ",
                $"session_key={entry.SessionKey}",
                $"session_id={entry.SubSessionId}",
                $"role={entry.Role}",
                $"label={entry.Label ?? ""}",
                $"messages={entry.MessageCount}",
                $"last_activity={entry.LastActivityAt:O}"));
        return ToolResult.Success($"Listed {entries.Count} sub-agent session(s)", string.Join(Environment.NewLine, lines));
    }
}
