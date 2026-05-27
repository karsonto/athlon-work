namespace Athlon.Agent.Mcp;

public static class McpClientDefaults
{
    /// <summary>Default JSON-RPC timeout for MCP initialize, tools/list, and tools/call.</summary>
    /// <remarks>Long-running vision tools may need health probe + inference; keep below app turn timeout (10 min).</remarks>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(9);
}
