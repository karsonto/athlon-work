using Athlon.Agent.Mcp;

namespace Athlon.Agent.App.ViewModels;

internal static class McpRuntimeStatusText
{
    public static string ToolSummary(McpServerStatus? status) =>
        status?.State switch
        {
            McpConnectionState.Connected => $"{status.Tools.Count} tools enabled",
            McpConnectionState.Connecting => "Connecting...",
            McpConnectionState.Error => $"Error · {status.LastError}",
            _ => "Not connected"
        };

    public static string SidebarSummary(McpServerStatus status) =>
        status.State switch
        {
            McpConnectionState.Connected => $"Connected · {status.Tools.Count} tools",
            McpConnectionState.Connecting => "Connecting...",
            McpConnectionState.Error => $"Error · {status.LastError}",
            _ => "Disabled"
        };
}
