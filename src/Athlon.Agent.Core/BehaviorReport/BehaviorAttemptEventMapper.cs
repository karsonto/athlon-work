namespace Athlon.Agent.Core.BehaviorReport;

/// <summary>
/// Maps <see cref="AgentAttemptEvent"/> to a behavior event id without recording twice.
/// Returns null when the attempt is handled by a more specific hook (e.g. skill_load).
/// </summary>
public static class BehaviorAttemptEventMapper
{
    public static (string EventId, Dictionary<string, object?> Parameters)? Map(AgentAttemptEvent attempt)
    {
        if (attempt.Kind == AgentAttemptKind.Model)
        {
            return (BehaviorEventIds.ModelCall, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["purpose"] = attempt.Purpose.ToString(),
                ["model"] = attempt.Model,
                ["prompt_tokens"] = attempt.Prompt,
                ["completion_tokens"] = attempt.Completion,
                ["latency_ms"] = attempt.Latency,
                ["result"] = attempt.Result,
                ["error_type"] = attempt.ErrorCode,
                ["session_id"] = attempt.SessionId
            });
        }

        var tool = attempt.Tool ?? "";
        // skill_load is recorded in LoadSkillThroughPathTool with skill_id/path.
        if (string.Equals(tool, "load_skill_through_path", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (IsMcpTool(tool))
        {
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool_name"] = tool,
                ["success"] = IsSuccess(attempt.Result),
                ["latency_ms"] = attempt.Latency,
                ["session_id"] = attempt.SessionId
            };

            if (McpToolNameCodec.TryDecode(tool, out var serverName, out var mcpTool))
            {
                parameters["server_name"] = serverName;
                parameters["tool_name"] = mcpTool;
                parameters["mode"] = "direct";
            }
            else if (IsMcpGatewayTool(tool))
            {
                parameters["gateway"] = tool;
                parameters["mode"] = "search";
            }

            return (BehaviorEventIds.McpTool, parameters);
        }

        return (BehaviorEventIds.ToolInvoke, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool_name"] = tool,
            ["success"] = IsSuccess(attempt.Result),
            ["latency_ms"] = attempt.Latency,
            ["session_id"] = attempt.SessionId
        });
    }

    private static bool IsSuccess(string result) =>
        string.Equals(result, "success", StringComparison.OrdinalIgnoreCase);

    private static bool IsMcpGatewayTool(string tool) =>
        string.Equals(tool, "mcp_search", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tool, "mcp_describe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tool, "mcp_call", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tool, "mcp_refresh_catalog", StringComparison.OrdinalIgnoreCase);

    private static bool IsMcpTool(string tool) =>
        IsMcpGatewayTool(tool)
        || tool.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase)
        || McpToolNameCodec.TryDecode(tool, out _, out _);
}
