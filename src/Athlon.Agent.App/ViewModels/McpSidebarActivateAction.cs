namespace Athlon.Agent.App.ViewModels;

public enum McpSidebarActivateAction
{
    Enable,
    Disable
}

public static class McpSidebarActivate
{
    public static McpSidebarActivateAction Resolve(bool enabled) =>
        enabled ? McpSidebarActivateAction.Disable : McpSidebarActivateAction.Enable;
}
