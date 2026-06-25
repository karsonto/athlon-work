using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SubAgentTool(
    AppSettings settings,
    Lazy<ISubAgentSessionManager> sessionManager,
    IActiveAgentSessionContext activeSessionContext) : IAgentTool, IExcludedFromChildAgentToolkit
{
    private readonly SubAgentSettings _subAgent = settings.SubAgent;

    public ToolDefinition Definition => BuildDefinition();

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!_subAgent.Enabled)
        {
            return ToolResult.Failure("Sub-agent disabled", "Sub-agent tool is disabled in settings.");
        }

        var parentSessionId = activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(parentSessionId))
        {
            return ToolResult.Failure("No parent session", "call_assistant requires an active parent agent session.");
        }

        if (_subAgent.MaxNestingDepth > 0 && SubAgentExecutionScope.CurrentDepth >= _subAgent.MaxNestingDepth)
        {
            return ToolResult.Failure(
                "Nesting limit",
                $"Sub-agent nesting depth limit ({_subAgent.MaxNestingDepth}) reached.");
        }

        if (!invocation.Arguments.TryGetValue("message", out var message) || string.IsNullOrWhiteSpace(message))
        {
            return ToolResult.Failure("Missing message", "Required parameter: message");
        }

        invocation.Arguments.TryGetValue("session_id", out var sessionIdArg);
        invocation.Arguments.TryGetValue("role", out var roleArg);

        if (!string.IsNullOrWhiteSpace(sessionIdArg))
        {
            var subSessionId = sessionIdArg.Trim();
            var sessionKey = SubAgentSessionKey.Build(parentSessionId, subSessionId);
            var send = await sessionManager.Value.SendAsync(
                parentSessionId,
                sessionKey,
                null,
                message.Trim(),
                _subAgent.DefaultSyncTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            if (!send.IsOk)
            {
                return ToolResult.Failure("Sub-agent failed", send.Error ?? send.Status);
            }

            return ToolResult.Success(
                $"Sub-agent completed (session_id={subSessionId})",
                send.Reply ?? $"session_id: {subSessionId}");
        }

        if (string.IsNullOrWhiteSpace(roleArg))
        {
            return ToolResult.Failure(
                "Missing role",
                "Provide role when starting a new sub-agent session, or pass session_id for an existing session.");
        }

        var spawn = await sessionManager.Value.SpawnAsync(
            parentSessionId,
            roleArg.Trim(),
            message.Trim(),
            null,
            _subAgent.DefaultSyncTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        if (!spawn.IsOk)
        {
            return ToolResult.Failure("Sub-agent failed", spawn.Error ?? spawn.Status);
        }

        var content = spawn.Reply ?? SubAgentResultFormatter.FormatSpawnInfo(spawn);
        return ToolResult.Success($"Sub-agent completed (session_id={spawn.SubSessionId})", content);
    }

    private ToolDefinition BuildDefinition()
    {
        var toolName = string.IsNullOrWhiteSpace(_subAgent.ToolName) ? "call_assistant" : _subAgent.ToolName.Trim();
        return new ToolDefinition(
            toolName,
            _subAgent.Description,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["role"] = "Who the child agent is: responsibilities, boundaries, and output style. Required for a new session_id; optional when continuing.",
                ["message"] = "Task instruction for this sub-agent turn.",
                ["session_id"] = "Optional. Omit to start a new sub-session; pass the id from a prior result to continue."
            },
            Group: ToolGroup.SubAgent);
    }
}
