using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

internal static class FileWriteToolArgumentsDisplay
{
    public const string FileWriteToolName = "file_write";
    public const string StreamingContentLabel = "content = 传输中…";

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

        if (ToolCallStreamingJsonHelper.TryExtractStringProperty(rawJson, ToolPathNormalizer.PathArgumentName, out path))
        {
            var length = ToolCallStreamingJsonHelper.TryEstimateStringPropertyLength(rawJson, "content", out var estimated)
                ? estimated
                : 0;
            return FormatFinal(path, length);
        }

        return "(无参数)";
    }

    public static string FormatArgumentsForPersistedDisplay(IReadOnlyDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue(ToolPathNormalizer.PathArgumentName, out var path) || string.IsNullOrWhiteSpace(path))
        {
            return "(无参数)";
        }

        arguments.TryGetValue("content", out var content);
        return FormatFinal(path, content?.Length ?? 0);
    }
}
