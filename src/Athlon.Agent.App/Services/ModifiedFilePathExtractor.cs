using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

internal static class ModifiedFilePathExtractor
{
    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal)
    {
        "file_edit",
        "file_write",
        "apply_patch"
    };

    public static bool IsFileTool(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && FileTools.Contains(toolName);

    public static string? ExtractPathFromArguments(string? argumentsJsonOrText)
    {
        if (string.IsNullOrWhiteSpace(argumentsJsonOrText) || string.Equals(argumentsJsonOrText, "…", StringComparison.Ordinal))
        {
            return null;
        }

        if (ToolCallStreamingJsonHelper.TryExtractStringProperty(argumentsJsonOrText, ToolPathNormalizer.PathArgumentName, out var streamingPath)
            && !string.IsNullOrWhiteSpace(streamingPath))
        {
            return ToolPathNormalizer.ForModel(streamingPath);
        }

        var trimmed = argumentsJsonOrText.TrimStart();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            var args = ToolCallArgumentsParser.ParseJson(argumentsJsonOrText);
            if (args.TryGetString(ToolPathNormalizer.PathArgumentName, out var jsonPath) && !string.IsNullOrWhiteSpace(jsonPath))
            {
                return ToolPathNormalizer.ForModel(jsonPath);
            }
        }

        foreach (var line in argumentsJsonOrText.Replace("\r\n", "\n").Split('\n'))
        {
            const string prefix = "path = ";
            var lineTrimmed = line.Trim();
            if (lineTrimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return ToolPathNormalizer.ForModel(lineTrimmed[prefix.Length..].Trim());
            }
        }

        return null;
    }

    public static IReadOnlyList<string> ExtractApplyPatchPaths(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var pastSummary = false;
        var pastSummaryBlank = false;
        var paths = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                pastSummary = true;
                continue;
            }

            if (pastSummary && !pastSummaryBlank && string.IsNullOrWhiteSpace(line))
            {
                pastSummaryBlank = true;
                continue;
            }

            if (!pastSummaryBlank || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("ToolCallId:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Tool `", StringComparison.Ordinal)
                || trimmed.StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            paths.Add(ToolPathNormalizer.ForModel(trimmed));
        }

        return paths;
    }

    public static ModifiedFileStatus ToModifiedFileStatus(ToolCallDisplayStatus status) => status switch
    {
        ToolCallDisplayStatus.Succeeded => ModifiedFileStatus.Succeeded,
        ToolCallDisplayStatus.Failed => ModifiedFileStatus.Failed,
        ToolCallDisplayStatus.Cancelled => ModifiedFileStatus.Failed,
        _ => ModifiedFileStatus.Pending
    };

    public static ModifiedFileStatus ParseResultStatus(string content)
    {
        ToolMessageDisplayParser.ParseToolContent(
            content,
            out _,
            out _,
            out var header,
            out _,
            out _,
            out _,
            out var status);
        _ = header;
        return ToModifiedFileStatus(status);
    }
}
