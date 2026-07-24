namespace Athlon.Agent.Mcp;

public static class McpClientDefaults
{
    /// <summary>Default JSON-RPC timeout for MCP initialize, tools/list, and tools/call.</summary>
    /// <remarks>Long-running vision tools may need health probe + inference; keep below app turn timeout (10 min).</remarks>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(9);

    /// <summary>
    /// Time to establish HTTP/SSE (TCP + first SSE <c>endpoint</c> event, or Streamable HTTP handshake).
    /// Kept short so a bad URL fails visibly instead of leaving the sidebar on "Not connected".
    /// </summary>
    public static readonly TimeSpan HttpConnectionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Overall MCP initialize handshake (stdio spawn or HTTP) before giving up.</summary>
    public static readonly TimeSpan ConnectInitializationTimeout = TimeSpan.FromSeconds(45);
}
