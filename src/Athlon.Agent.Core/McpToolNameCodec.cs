namespace Athlon.Agent.Core;

public static class McpToolNameCodec
{
    public const string Prefix = "mcp.";

    public static string Encode(string serverName, string toolName)
    {
        serverName = (serverName ?? string.Empty).Trim();
        toolName = (toolName ?? string.Empty).Trim();
        return $"{Prefix}{serverName}.{toolName}";
    }

    public static bool TryDecode(string? encoded, out string serverName, out string toolName)
    {
        serverName = string.Empty;
        toolName = string.Empty;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        var value = encoded.Trim();
        if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = value[Prefix.Length..];
        var dot = rest.IndexOf('.');
        if (dot <= 0 || dot >= rest.Length - 1)
        {
            return false;
        }

        serverName = rest[..dot];
        toolName = rest[(dot + 1)..];
        return !string.IsNullOrWhiteSpace(serverName) && !string.IsNullOrWhiteSpace(toolName);
    }
}

