using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SessionsPendingCompletionsTool(
    AppSettings settings,
    Lazy<ISubAgentSessionManager> sessionManager,
    IActiveAgentSessionContext activeSessionContext) : IAgentTool, ISubAgentTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        Name: "sessions_pending_completions",
        Description:
            "Drain completed sub-agent background tasks and async runs. "
            + "Call at the start of a turn when you used timeout_seconds=0.",
        ParametersSchema: ToolSchema.Object()
            .Integer("limit", "Max completions to drain (default 5).", defaultValue: 5, minimum: 1, maximum: 100)
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
            return ToolResult.Failure("No parent session", "sessions_pending_completions requires an active parent session.");
        }

        var limit = 5;
        if (invocation.Arguments.TryGetInt32("limit", out var parsed))
        {
            limit = parsed;
        }

        var completions = await sessionManager.Value.DrainCompletionsAsync(parentSessionId, limit, cancellationToken).ConfigureAwait(false);
        if (completions.Count == 0)
        {
            return ToolResult.Success("No pending completions", "(none)");
        }

        var body = string.Join(
            Environment.NewLine + Environment.NewLine + "---" + Environment.NewLine + Environment.NewLine,
            completions.Select(completion => completion.AnnounceText));
        return ToolResult.Success($"Drained {completions.Count} completion(s)", body);
    }
}
