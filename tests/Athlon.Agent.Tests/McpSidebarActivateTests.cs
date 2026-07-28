using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

public sealed class McpSidebarActivateTests
{
    [Theory]
    [InlineData(false, McpConnectionState.Disabled, McpSidebarActivateAction.Enable)]
    [InlineData(false, McpConnectionState.Connected, McpSidebarActivateAction.Enable)]
    [InlineData(false, McpConnectionState.Error, McpSidebarActivateAction.Enable)]
    [InlineData(true, McpConnectionState.Connected, McpSidebarActivateAction.Disable)]
    [InlineData(true, McpConnectionState.Error, McpSidebarActivateAction.Reconnect)]
    [InlineData(true, McpConnectionState.Connecting, McpSidebarActivateAction.Reconnect)]
    [InlineData(true, McpConnectionState.Disabled, McpSidebarActivateAction.Reconnect)]
    public void Resolve_ReturnsExpectedAction(
        bool enabled,
        McpConnectionState runtimeState,
        McpSidebarActivateAction expected)
    {
        var action = McpSidebarActivate.Resolve(enabled, runtimeState);
        Assert.Equal(expected, action);
    }
}
