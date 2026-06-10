using Athlon.Agent.Core;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Services;

internal static class ToolMessageDisplayParser
{
    public static void ParseToolContent(
        string content,
        out string? toolCallId,
        out string toolName,
        out string header,
        out string summary,
        out string detail,
        out string argumentsText,
        out ToolCallDisplayStatus status)
    {
        toolCallId = null;
        toolName = string.Empty;
        argumentsText = string.Empty;
        status = ToolCallDisplayStatus.Succeeded;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        header = "工具调用";
        summary = string.Empty;

        foreach (var line in lines)
        {
            if (line.StartsWith("ToolCallId:", StringComparison.OrdinalIgnoreCase))
            {
                toolCallId = line["ToolCallId:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase))
            {
                argumentsText = FormatArgumentsFromPersistedLine(line["Arguments:".Length..].Trim());
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line) && header == "工具调用" && line.StartsWith("Tool `", StringComparison.Ordinal))
            {
                header = line.Trim();
                toolName = TryParseToolName(header);
                status = ParseToolStatus(header);
                continue;
            }

            if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                summary = line["Summary:".Length..].Trim();
            }
        }

        detail = content.Trim();
        if (detail.Contains("[Tool result evicted", StringComparison.OrdinalIgnoreCase))
        {
            header = $"① 工具结果归档 · {header}";
        }
    }

    public static string TryParseToolName(string header)
    {
        const string prefix = "Tool `";
        var start = header.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += prefix.Length;
        var end = header.IndexOf('`', start);
        return end > start ? header[start..end] : string.Empty;
    }

    public static ToolCallDisplayStatus ParseToolStatus(string header)
    {
        if (header.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return ToolCallDisplayStatus.Failed;
        }

        if (header.Contains("succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return ToolCallDisplayStatus.Succeeded;
        }

        return ToolCallDisplayStatus.Succeeded;
    }

    public static string FormatArgumentsFull(IReadOnlyDictionary<string, string> arguments) =>
        arguments.Count == 0
            ? "(无参数)"
            : string.Join(
                Environment.NewLine,
                arguments.Select(argument =>
                {
                    var value = string.Equals(argument.Key, ToolPathNormalizer.PathArgumentName, StringComparison.OrdinalIgnoreCase)
                        ? ToolPathNormalizer.ForModel(argument.Value)
                        : argument.Value;
                    return $"{argument.Key} = {value}";
                }));

    public static string FormatArgumentsFromPersistedLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || string.Equals(line, "(none)", StringComparison.OrdinalIgnoreCase))
        {
            return "(无参数)";
        }

        if (!line.Contains(';'))
        {
            return line.Replace("=", " = ", StringComparison.Ordinal);
        }

        return string.Join(
            Environment.NewLine,
            line.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                {
                    var separator = part.IndexOf('=');
                    return separator < 0 ? part : $"{part[..separator].Trim()} = {part[(separator + 1)..].Trim()}";
                }));
    }
}
