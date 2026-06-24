namespace Athlon.Agent.Core;

public enum ToolGroup
{
    Builtin,
    Mcp,
    SubAgent
}

public enum ToolInvocationPolicy
{
    Allow,
    Ask,
    Deny
}

public sealed record PendingToolApproval(
    string ToolCallId,
    string ToolName,
    IReadOnlyDictionary<string, string> Arguments,
    ToolInvocationPolicy RequestedPolicy,
    DateTimeOffset RequestedAt);

public static class ToolInvocationPolicyEnforcer
{
    public static ToolResult? TryBlockInvocation(ToolDefinition definition)
    {
        if (definition.InvocationPolicy == ToolInvocationPolicy.Deny)
        {
            return ToolResult.Failure(
                "Tool invocation denied",
                $"Tool '{definition.Name}' is not allowed by policy.");
        }

        return null;
    }

    public static PendingToolApproval? TryCreatePendingApproval(AgentToolCall toolCall, ToolDefinition definition)
    {
        if (definition.InvocationPolicy != ToolInvocationPolicy.Ask && !definition.RequiresApproval)
        {
            return null;
        }

        return new PendingToolApproval(
            toolCall.Id,
            toolCall.Name,
            toolCall.Arguments,
            definition.InvocationPolicy == ToolInvocationPolicy.Ask
                ? ToolInvocationPolicy.Ask
                : ToolInvocationPolicy.Allow,
            DateTimeOffset.UtcNow);
    }
}
