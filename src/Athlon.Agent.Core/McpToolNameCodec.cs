using System.Text.RegularExpressions;

namespace Athlon.Agent.Core;

/// <summary>
/// Encodes MCP tools as a single function name for OpenAI-compatible APIs.
/// Names must match <c>^[a-zA-Z0-9_-]+$</c> (no dots).
/// Format: <c>mcp_{server}__{tool}</c>
/// </summary>
public static partial class McpToolNameCodec
{
    public const string Prefix = "mcp_";
    private const string Separator = "__";
    private const string SeparatorEscape = "_2_";

    [GeneratedRegex("^[a-zA-Z0-9_-]+$")]
    private static partial Regex ApiToolNamePattern();

    public static string Encode(string serverName, string toolName)
    {
        serverName = (serverName ?? string.Empty).Trim();
        toolName = (toolName ?? string.Empty).Trim();
        var encoded = $"{Prefix}{EscapeSegment(serverName)}{Separator}{EscapeSegment(toolName)}";
        if (!ApiToolNamePattern().IsMatch(encoded))
        {
            throw new ArgumentException(
                $"Encoded MCP tool name '{encoded}' is not API-compatible. Server/tool names may only use letters, digits, '_', and '-'.",
                nameof(serverName));
        }

        return encoded;
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
        if (TryDecodeCurrent(value, out serverName, out toolName))
        {
            return true;
        }

        return TryDecodeLegacy(value, out serverName, out toolName);
    }

    private static bool TryDecodeCurrent(string value, out string serverName, out string toolName)
    {
        serverName = string.Empty;
        toolName = string.Empty;
        if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = value[Prefix.Length..];
        var separatorIndex = rest.IndexOf(Separator, StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex >= rest.Length - Separator.Length)
        {
            return false;
        }

        serverName = UnescapeSegment(rest[..separatorIndex]);
        toolName = UnescapeSegment(rest[(separatorIndex + Separator.Length)..]);
        return !string.IsNullOrWhiteSpace(serverName) && !string.IsNullOrWhiteSpace(toolName);
    }

    /// <summary>Legacy format: mcp.server.tool (dots — rejected by some API providers).</summary>
    private static bool TryDecodeLegacy(string value, out string serverName, out string toolName)
    {
        serverName = string.Empty;
        toolName = string.Empty;
        const string legacyPrefix = "mcp.";
        if (!value.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = value[legacyPrefix.Length..];
        var dot = rest.IndexOf('.');
        if (dot <= 0 || dot >= rest.Length - 1)
        {
            return false;
        }

        serverName = rest[..dot];
        toolName = rest[(dot + 1)..];
        return !string.IsNullOrWhiteSpace(serverName) && !string.IsNullOrWhiteSpace(toolName);
    }

    private static string EscapeSegment(string value) => value.Replace(Separator, SeparatorEscape, StringComparison.Ordinal);

    private static string UnescapeSegment(string value) => value.Replace(SeparatorEscape, Separator, StringComparison.Ordinal);
}
