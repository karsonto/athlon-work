using System.Text.Json;
using System.Text.RegularExpressions;

namespace Athlon.Agent.Core;

/// <summary>
/// Lightweight extraction from streaming / partial tool-call argument JSON without full parse.
/// </summary>
public static class ToolCallStreamingJsonHelper
{
    /// <summary>
    /// Prefix scanned for a display path. Edit payloads put <c>path</c> or the patch file header
    /// first, so the window stays fixed instead of copying the growing body.
    /// </summary>
    public const int EditPathScanWindow = 8192;

    private static readonly string[] ApplyPatchFileMarkers =
    [
        "*** Update File:",
        "*** Add File:",
        "*** Delete File:"
    ];

    private static readonly Regex StringPropertyRegex = new(
        "\"(?<name>[A-Za-z_][A-Za-z0-9_]*)\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsEditTool(string? toolName) =>
        toolName is "file_edit" or "file_write" or "apply_patch";

    public static string PathOnlyArgumentsJson(string path) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [ToolPathNormalizer.PathArgumentName] = path
        });

    /// <summary>
    /// Path to show while an edit tool is still streaming. <c>file_edit</c> / <c>file_write</c> use
    /// the <c>path</c> property; <c>apply_patch</c> also accepts the first V4A file header.
    /// </summary>
    public static bool TryExtractEditDisplayPath(string? partialJson, string? toolName, out string path)
    {
        if (TryExtractStringProperty(partialJson, ToolPathNormalizer.PathArgumentName, out path)
            && !string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        if (string.Equals(toolName, "apply_patch", StringComparison.Ordinal)
            && TryExtractApplyPatchFile(partialJson, out path))
        {
            return true;
        }

        path = string.Empty;
        return false;
    }

    public static bool TryExtractApplyPatchFile(string? partial, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(partial))
        {
            return false;
        }

        var matchIndex = -1;
        var markerLength = 0;
        foreach (var marker in ApplyPatchFileMarkers)
        {
            var index = partial.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            if (matchIndex < 0 || index < matchIndex)
            {
                matchIndex = index;
                markerLength = marker.Length;
            }
        }

        if (matchIndex < 0)
        {
            return false;
        }

        var start = matchIndex + markerLength;
        while (start < partial.Length && partial[start] == ' ')
        {
            start++;
        }

        var end = start;
        while (end < partial.Length
               && partial[end] is not ('\n' or '\r' or '"' or '\\'))
        {
            end++;
        }

        if (end == start)
        {
            return false;
        }

        path = partial[start..end].Trim();
        return path.Length > 0;
    }

    public static bool TryExtractStringProperty(string? partialJson, string propertyName, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(partialJson) || string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        foreach (Match match in StringPropertyRegex.Matches(partialJson))
        {
            if (!string.Equals(match.Groups["name"].Value, propertyName, StringComparison.Ordinal))
            {
                continue;
            }

            value = UnescapeJsonString(match.Groups["value"].Value);
            return true;
        }

        return false;
    }

    public static bool TryEstimateStringPropertyLength(string? partialJson, string propertyName, out int length)
    {
        length = 0;
        if (string.IsNullOrWhiteSpace(partialJson) || string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var marker = $"\"{propertyName}\":\"";
        var startIndex = partialJson.IndexOf(marker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            marker = $"\"{propertyName}\": \"";
            startIndex = partialJson.IndexOf(marker, StringComparison.Ordinal);
        }

        if (startIndex < 0)
        {
            marker = $"\"{propertyName}\" : \"";
            startIndex = partialJson.IndexOf(marker, StringComparison.Ordinal);
        }

        if (startIndex < 0)
        {
            return false;
        }

        var index = startIndex + marker.Length;
        length = CountJsonStringContentLength(partialJson, index);
        return true;
    }

    public static bool TryParseCompleteFileWriteArgs(string? json, out string path, out string content)
    {
        path = string.Empty;
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty(ToolPathNormalizer.PathArgumentName, out var pathElement)
                || pathElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            path = pathElement.GetString() ?? string.Empty;
            if (document.RootElement.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                content = contentElement.GetString() ?? string.Empty;
            }

            return !string.IsNullOrWhiteSpace(path);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int CountJsonStringContentLength(string text, int startIndex)
    {
        var length = 0;
        for (var i = startIndex; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                if (i + 1 >= text.Length)
                {
                    length++;
                    break;
                }

                length++;
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                break;
            }

            length++;
        }

        return length;
    }

    private static string UnescapeJsonString(string value) =>
        value
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);
}
