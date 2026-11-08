using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

/// <summary>Reads tool results and tool-call arguments into a unified diff for the review pane.</summary>
internal static class ToolResultDiffExtractor
{
    internal static void TryAttachDiff(
        ModifiedFileViewModel file,
        string? toolName,
        string? argumentsText,
        string? toolContent)
    {
        var diff = ExtractDiff(toolName, argumentsText, toolContent, file.RelativePath);
        if (string.IsNullOrWhiteSpace(diff))
        {
            return;
        }

        // Whole-file rewrites replace; incremental edits accumulate.
        if (string.Equals(toolName, "file_write", StringComparison.Ordinal))
        {
            file.SetDiff(diff);
        }
        else
        {
            file.AppendDiff(diff);
        }
    }

    internal static string? ExtractDiff(
        string? toolName,
        string? argumentsText,
        string? toolContent,
        string relativePath)
    {
        if (string.Equals(toolName, "file_edit", StringComparison.Ordinal))
        {
            var body = StripToolResultBody(toolContent);
            return LooksLikeUnifiedDiff(body) ? body : null;
        }

        if (string.Equals(toolName, "file_write", StringComparison.Ordinal))
        {
            // Persisted results keep the full body after "content=", spanning lines until Summary.
            // The display arguments line only has the path, so the review pane has to read the body.
            if (TryExtractPersistedFileWriteContent(toolContent, out var persisted)
                && !IsFileWriteContentPlaceholder(persisted))
            {
                return UnifiedDiffGenerator.Generate(string.Empty, persisted, relativePath);
            }

            if (TryExtractFileWriteContent(argumentsText, out var content)
                && !IsFileWriteContentPlaceholder(content))
            {
                return UnifiedDiffGenerator.Generate(string.Empty, content, relativePath);
            }

            return null;
        }

        if (string.Equals(toolName, "apply_patch", StringComparison.Ordinal))
        {
            if (TryExtractPatchArgument(argumentsText, out var patch) && LooksLikeUnifiedDiff(patch))
            {
                return patch;
            }

            var body = StripToolResultBody(toolContent);
            return LooksLikeUnifiedDiff(body) ? body : null;
        }

        return null;
    }

    /// <summary>Mirrors <c>ModelMessageBuilder.StripToolCallIdAndMetadata</c> for App-layer use.</summary>
    private static string StripToolResultBody(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var startIndex = 0;

        if (lines.Length > startIndex && lines[startIndex].StartsWith("ToolCallId:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Tool `", StringComparison.Ordinal))
            startIndex++;
        if (lines.Length > startIndex && lines[startIndex].Length == 0)
            startIndex++;
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        if (lines.Length > startIndex && lines[startIndex].Length == 0)
            startIndex++;

        if (startIndex >= lines.Length)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, lines[startIndex..]);
    }

    private static bool TryExtractFileWriteContent(string? argumentsText, out string content)
    {
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(argumentsText))
        {
            return false;
        }

        if (ToolCallStreamingJsonHelper.TryParseCompleteFileWriteArgs(argumentsText, out _, out content)
            && content is not null)
        {
            return true;
        }

        if (ToolCallStreamingJsonHelper.TryExtractStringProperty(argumentsText, "content", out content)
            && !string.IsNullOrEmpty(content))
        {
            return true;
        }

        foreach (var line in argumentsText.Replace("\r\n", "\n").Split('\n'))
        {
            const string prefix = "content = ";
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                content = trimmed[prefix.Length..];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a file_write body out of a stored tool result. The arguments header is
    /// <c>path=...</c> on the Arguments line and <c>content=</c> on the following lines, through
    /// the Summary line. A one-line <c>content = hello</c> form is accepted too.
    /// </summary>
    private static bool TryExtractPersistedFileWriteContent(string? toolContent, out string content)
    {
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(toolContent))
        {
            return false;
        }

        var lines = toolContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var contentLine = -1;
        var first = string.Empty;
        var summaryLine = lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            if (contentLine < 0 && TryTakeContentAssignment(lines[i], out first))
            {
                contentLine = i;
                continue;
            }

            if (contentLine >= 0 && lines[i].StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                summaryLine = i;
                break;
            }
        }

        if (contentLine < 0)
        {
            return false;
        }

        if (summaryLine == contentLine + 1)
        {
            content = first;
            return true;
        }

        var tail = string.Join("\n", lines[(contentLine + 1)..summaryLine]);
        content = string.IsNullOrEmpty(tail) ? first : first + "\n" + tail;
        return true;
    }

    private static bool TryTakeContentAssignment(string line, out string rest)
    {
        rest = string.Empty;
        var source = line.StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase)
            ? line["Arguments:".Length..]
            : line;
        var index = source.IndexOf("content", StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var after = source[(index + "content".Length)..].TrimStart();
            if (after.StartsWith('='))
            {
                rest = after[1..].TrimStart();
                return true;
            }

            index = source.IndexOf("content", index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsFileWriteContentPlaceholder(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.Length < 8 || trimmed[0] != '(' || trimmed[^1] != ')')
        {
            return false;
        }

        var inner = trimmed[1..^1];
        var space = inner.IndexOf(' ');
        if (space <= 0 || !string.Equals(inner[space..], " chars", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 0; i < space; i++)
        {
            if (!char.IsDigit(inner[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryExtractPatchArgument(string? argumentsText, out string patch)
    {
        patch = string.Empty;
        if (string.IsNullOrWhiteSpace(argumentsText))
        {
            return false;
        }

        if (ToolCallStreamingJsonHelper.TryExtractStringProperty(argumentsText, "patch", out patch)
            && !string.IsNullOrWhiteSpace(patch))
        {
            return true;
        }

        foreach (var line in argumentsText.Replace("\r\n", "\n").Split('\n'))
        {
            const string prefix = "patch = ";
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                patch = trimmed[prefix.Length..];
                return !string.IsNullOrWhiteSpace(patch);
            }
        }

        return false;
    }

    private static bool LooksLikeUnifiedDiff(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("\n@@", StringComparison.Ordinal)
            || text.Contains("\r\n@@", StringComparison.Ordinal)
            || text.StartsWith("@@", StringComparison.Ordinal)
            || text.StartsWith("--- ", StringComparison.Ordinal)
            || text.StartsWith("diff --git", StringComparison.Ordinal);
    }
}
