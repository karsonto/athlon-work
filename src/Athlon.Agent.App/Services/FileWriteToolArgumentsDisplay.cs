using Athlon.Agent.App.Resources;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

internal static class FileWriteToolArgumentsDisplay
{
    public const string FileWriteToolName = "file_write";
    public static string StreamingContentLabel => Strings.Get("FileWrite_StreamingContent");

    public static bool IsFileWrite(string? toolName) =>
        string.Equals(toolName, FileWriteToolName, StringComparison.Ordinal);

    public static string FormatPath(string path) =>
        $"{ToolPathNormalizer.PathArgumentName} = {ToolPathNormalizer.ForModel(path)}";

    public static string FormatStreaming(string path) =>
        $"{FormatPath(path)}{Environment.NewLine}{StreamingContentLabel}";

    /// <summary>End-of-call line for file_edit / apply_patch: path only, never the edit body.</summary>
    public static string FormatEditPathSummary(string? rawJson, string? toolName)
    {
        if (ToolCallStreamingJsonHelper.TryExtractEditDisplayPath(rawJson, toolName, out var path))
        {
            return FormatPath(path);
        }

        return Strings.Get("Tool_NoArgs");
    }

    public static string FormatFinal(string path, int contentCharCount) =>
        $"{ToolPathNormalizer.PathArgumentName} = {ToolPathNormalizer.ForModel(path)}{Environment.NewLine}content = ({contentCharCount} chars)";

    public static string FormatFinalFromRawJson(string? rawJson)
    {
        if (ToolCallStreamingJsonHelper.TryParseCompleteFileWriteArgs(rawJson, out var path, out var content))
        {
            if (!HasContentProperty(rawJson))
            {
                return FormatPath(path);
            }

            return FormatFinal(path, content.Length);
        }

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            // Incomplete/invalid JSON: do not surface a regex-extracted path with content=(0 chars).
            return Strings.Get("FileWrite_ArgumentsJsonInvalid");
        }

        return Strings.Get("Tool_NoArgs");
    }

    public static string FormatArgumentsForPersistedDisplay(ToolCallArguments arguments)
    {
        if (!arguments.TryGetString(ToolPathNormalizer.PathArgumentName, out var path) || string.IsNullOrWhiteSpace(path))
        {
            return Strings.Get("Tool_NoArgs");
        }

        arguments.TryGetString("content", out var content);
        return FormatFinal(path, content?.Length ?? 0);
    }

    private static bool HasContentProperty(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(rawJson);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("content", out _);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
