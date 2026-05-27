namespace Athlon.Agent.Mcp;

public static class McpTransportKinds
{
    public const string Stdio = "stdio";

    /// <summary>MCP Streamable HTTP (POST + optional SSE on same endpoint).</summary>
    public const string StreamableHttp = "http";

    public static bool IsStdio(string? transportType) =>
        string.IsNullOrWhiteSpace(transportType)
        || string.Equals(transportType.Trim(), Stdio, StringComparison.OrdinalIgnoreCase);

    public static bool IsStreamableHttp(string? transportType)
    {
        if (string.IsNullOrWhiteSpace(transportType))
        {
            return false;
        }

        var t = transportType.Trim();
        return t.Equals("http", StringComparison.OrdinalIgnoreCase)
               || t.Equals("https", StringComparison.OrdinalIgnoreCase)
               || t.Equals("streamable-http", StringComparison.OrdinalIgnoreCase)
               || t.Equals("streamable_http", StringComparison.OrdinalIgnoreCase)
               || t.Equals("streamablehttp", StringComparison.OrdinalIgnoreCase)
               || t.Equals("sse", StringComparison.OrdinalIgnoreCase);
    }
}
