using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ToolInvocationPolicyTests
{
    [Fact]
    public void TryBlockInvocation_BlocksDenyPolicy()
    {
        var definition = new ToolDefinition(
            "blocked",
            "blocked tool",
            ToolSchema.Object().Build(),
            InvocationPolicy: ToolInvocationPolicy.Deny);

        var result = ToolInvocationPolicyEnforcer.TryBlockInvocation(definition);

        Assert.NotNull(result);
        Assert.False(result!.Succeeded);
    }

    [Fact]
    public void TryBlockInvocation_RequiresExplicitApprovalForAskPolicy()
    {
        var definition = new ToolDefinition(
            "execute_command",
            "cmd",
            ToolSchema.Object().Build(),
            InvocationPolicy: ToolInvocationPolicy.Ask);

        var pending = ToolInvocationPolicyEnforcer.TryBlockInvocation(definition);
        var approved = ToolInvocationPolicyEnforcer.TryBlockInvocation(
            definition,
            ToolApprovalDecision.Approved);

        Assert.NotNull(pending);
        Assert.Contains("policy.approval_required", pending!.Error, StringComparison.Ordinal);
        Assert.Null(approved);
    }

    [Fact]
    public void TryCreatePendingApproval_ReturnsRecordForAskTools()
    {
        var definition = new ToolDefinition(
            "execute_command",
            "cmd",
            ToolSchema.Object().Build(),
            InvocationPolicy: ToolInvocationPolicy.Ask);
        var call = new AgentToolCall("call-1", "execute_command", new Dictionary<string, string> { ["command"] = "dir" });

        var pending = ToolInvocationPolicyEnforcer.TryCreatePendingApproval(call, definition);

        Assert.NotNull(pending);
        Assert.Equal("call-1", pending!.ToolCallId);
        Assert.Equal(ToolInvocationPolicy.Ask, pending.RequestedPolicy);
    }
}
