namespace Athlon.Agent.Core;

public static class ToolApprovalGate
{
    public static bool IsApprovalRequired(
        AppSettings settings,
        AgentRunContext? runContext,
        AgentToolCall toolCall,
        ToolDefinition definition)
    {
        var effective = runContext?.RequireToolApproval ?? settings.ToolPermissions.RequireToolApproval;
        if (!effective)
        {
            return false;
        }

        return ToolInvocationPolicyEnforcer.TryCreatePendingApproval(toolCall, definition) is not null;
    }

    public static ToolResult CreateRejectedResult() =>
        ToolResult.Failure(
            "Tool invocation rejected",
            "The user declined this tool call. Choose a different approach or ask for clarification.");

    public static ToolResult CreateNoHandlerResult() =>
        ToolResult.Failure(
            "Tool approval required",
            "Tool approval is enabled but no approval handler is available for this run.");
}
