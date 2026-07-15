using Athlon.Agent.App.Resources;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

internal static class FileWriteToolArgumentsDisplay
{
    public const string FileWriteToolName = "file_write";
    public static string StreamingContentLabel => Strings.Get("FileWrite_StreamingContent");

    public static bool IsFileWrite(string? toolName) =>
        string.Equals(toolName, FileWriteToolName, StringComparison.Ordinal);

    public static string FormatStreaming(string path) =>
        $"{ToolPathNormalizer.PathArgumentName} = {ToolPathNormalizer.ForModel(path)}{Environment.NewLine}{StreamingContentLabel}";

    public static string FormatFinal(string path, int contentCharCount) =>
        $"{ToolPathNormalizer.PathArgumentName} = {ToolPathNormalizer.ForModel(path)}{Environment.NewLine}content = ({contentCharCount} chars)";

    public static string FormatFinalFromRawJson(string? rawJson)
    {
        if (ToolCallStreamingJsonHelper.TryParseCompleteFileWriteArgs(rawJson, out var path, out var content))
        {
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
}
