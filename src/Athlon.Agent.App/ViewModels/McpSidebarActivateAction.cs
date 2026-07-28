using Athlon.Agent.Mcp;

namespace Athlon.Agent.App.ViewModels;

public enum McpSidebarActivateAction
{
    Enable,
    Disable,
    Reconnect
}

public static class McpSidebarActivate
{
    public static McpSidebarActivateAction Resolve(bool enabled, McpConnectionState runtimeState)
    {
        if (!enabled)
        {
            return McpSidebarActivateAction.Enable;
        }

        return runtimeState == McpConnectionState.Connected
            ? McpSidebarActivateAction.Disable
            : McpSidebarActivateAction.Reconnect;
    }
}
