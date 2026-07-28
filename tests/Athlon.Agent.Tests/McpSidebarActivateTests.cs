using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.Tests;

public sealed class McpSidebarActivateTests
{
    [Theory]
    [InlineData(false, McpSidebarActivateAction.Enable)]
    [InlineData(true, McpSidebarActivateAction.Disable)]
    public void Resolve_TogglesEnabledState(bool enabled, McpSidebarActivateAction expected)
    {
        var action = McpSidebarActivate.Resolve(enabled);
        Assert.Equal(expected, action);
    }
}
