using ModelContextProtocol.Client;

namespace Athlon.Agent.Mcp;

public static class McpTransportKinds
{
    public const string Stdio = "stdio";

    /// <summary>Generic HTTP MCP type (Claude Desktop <c>http</c>); resolved via auto-detect.</summary>
    public const string StreamableHttp = "http";

    /// <summary>Classic HTTP+SSE MCP type.</summary>
    public const string Sse = "sse";

    public static bool IsStdio(string? transportType) =>
        string.IsNullOrWhiteSpace(transportType)
        || string.Equals(transportType.Trim(), Stdio, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Remote HTTP MCP: Streamable HTTP, classic HTTP+SSE, or generic <c>http</c>/<c>https</c>.
    /// </summary>
    public static bool IsHttp(string? transportType)
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
               || t.Equals(Sse, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc cref="IsHttp"/>
    public static bool IsStreamableHttp(string? transportType) => IsHttp(transportType);

    public static bool IsExplicitSse(string? transportType) =>
        !string.IsNullOrWhiteSpace(transportType)
        && transportType.Trim().Equals(Sse, StringComparison.OrdinalIgnoreCase);

    public static bool IsExplicitStreamableHttp(string? transportType)
    {
        if (string.IsNullOrWhiteSpace(transportType))
        {
            return false;
        }

        var t = transportType.Trim();
        return t.Equals("streamable-http", StringComparison.OrdinalIgnoreCase)
               || t.Equals("streamable_http", StringComparison.OrdinalIgnoreCase)
               || t.Equals("streamablehttp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Choose SDK HTTP mode: explicit SSE / Streamable HTTP, URL hint (<c>/sse</c>), otherwise auto-detect.
    /// </summary>
    public static HttpTransportMode ResolveHttpTransportMode(string? transportType, string? url)
    {
        if (IsExplicitSse(transportType) || LooksLikeSseEndpoint(url))
        {
            return HttpTransportMode.Sse;
        }

        if (IsExplicitStreamableHttp(transportType))
        {
            return HttpTransportMode.StreamableHttp;
        }

        // Generic http/https: try Streamable HTTP, then fall back to classic SSE.
        return HttpTransportMode.AutoDetect;
    }

    public static string FormatHttpTransportLabel(HttpTransportMode mode) => mode switch
    {
        HttpTransportMode.Sse => Sse,
        HttpTransportMode.StreamableHttp => "streamable-http",
        _ => "http-auto"
    };

    public static bool LooksLikeSseEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/sse", StringComparison.OrdinalIgnoreCase);
    }
}
