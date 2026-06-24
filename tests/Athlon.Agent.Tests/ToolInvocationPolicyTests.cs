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
            new Dictionary<string, string>(),
            InvocationPolicy: ToolInvocationPolicy.Deny);

        var result = ToolInvocationPolicyEnforcer.TryBlockInvocation(definition);

        Assert.NotNull(result);
        Assert.False(result!.Succeeded);
    }

    [Fact]
    public void TryBlockInvocation_AllowsAskPolicy()
    {
        var definition = new ToolDefinition(
            "execute_command",
            "cmd",
            new Dictionary<string, string>(),
            InvocationPolicy: ToolInvocationPolicy.Ask);

        Assert.Null(ToolInvocationPolicyEnforcer.TryBlockInvocation(definition));
    }

    [Fact]
    public void TryCreatePendingApproval_ReturnsRecordForAskTools()
    {
        var definition = new ToolDefinition(
            "execute_command",
            "cmd",
            new Dictionary<string, string>(),
            InvocationPolicy: ToolInvocationPolicy.Ask);
        var call = new AgentToolCall("call-1", "execute_command", new Dictionary<string, string> { ["command"] = "dir" });

        var pending = ToolInvocationPolicyEnforcer.TryCreatePendingApproval(call, definition);

        Assert.NotNull(pending);
        Assert.Equal("call-1", pending!.ToolCallId);
        Assert.Equal(ToolInvocationPolicy.Ask, pending.RequestedPolicy);
    }
}
