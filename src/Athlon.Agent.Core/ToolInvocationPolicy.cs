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

public enum ToolApprovalDecision
{
    None,
    Pending,
    Approved,
    Denied
}

public sealed record PendingToolApproval(
    string ToolCallId,
    string ToolName,
    ToolCallArguments Arguments,
    ToolInvocationPolicy RequestedPolicy,
    DateTimeOffset RequestedAt);

public static class ToolInvocationPolicyEnforcer
{
    public static bool RequiresApproval(ToolDefinition definition) =>
        definition.InvocationPolicy == ToolInvocationPolicy.Ask || definition.RequiresApproval;

    public static ToolResult? TryBlockInvocation(
        ToolDefinition definition,
        ToolApprovalDecision approvalDecision = ToolApprovalDecision.None,
        PendingToolApproval? pendingApproval = null)
    {
        if (definition.InvocationPolicy == ToolInvocationPolicy.Deny)
        {
            return ToolInvocationErrors.Failure(
                "Tool invocation denied",
                new ToolInvocationError(
                    "policy.denied",
                    "$",
                    "tool policy Allow or approved Ask",
                    "Deny",
                    $"Do not call `{definition.Name}`; choose an allowed alternative."));
        }

        if (!RequiresApproval(definition))
        {
            return null;
        }

        if (approvalDecision == ToolApprovalDecision.Approved)
        {
            return null;
        }

        if (approvalDecision == ToolApprovalDecision.Denied)
        {
            return ToolInvocationErrors.Failure(
                "Tool approval denied",
                new ToolInvocationError(
                    "policy.approval_denied",
                    "$",
                    "explicit user approval",
                    "denied",
                    $"Do not execute `{definition.Name}` unless the user later requests it again."));
        }

        return ToolInvocationErrors.Failure(
            "Tool approval pending",
            new ToolInvocationError(
                "policy.approval_required",
                "$",
                "explicit user approval",
                approvalDecision == ToolApprovalDecision.Pending ? "pending" : "not requested",
                pendingApproval is null
                    ? $"Request approval before executing `{definition.Name}`."
                    : $"Present approval request `{pendingApproval.ToolCallId}` to the user; do not execute until approved."));
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
