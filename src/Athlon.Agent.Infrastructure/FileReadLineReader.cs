using System.Text;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal static class FileReadLineReader
{
    internal const string LineTruncatedSuffix = "... [line truncated]";
    internal const string MetaHeader = "--- file_read meta ---";

    internal sealed record Selection(int StartLine, int EndLine);

    internal sealed record ReadResult(
        string Body,
        int TotalLines,
        int LinesReturned,
        int StartLine,
        bool Truncated,
        int? NextStartLine);

    internal static Selection ResolveSelection(ToolInvocation invocation, FileReadSettings settings)
    {
        var startLine = ToolArguments.GetInt32(invocation, "start_line", 1);
        var endLine = ToolArguments.GetInt32(invocation, "end_line", 0);
        startLine = Math.Max(1, startLine);
        if (endLine < startLine)
        {
            endLine = startLine + settings.DefaultLineLimit - 1;
        }

        endLine = Math.Min(endLine, startLine + settings.MaxLinesPerCall - 1);
        return new Selection(startLine, endLine);
    }

    internal static async Task<ReadResult> ReadAsync(
        string fullPath,
        Selection selection,
        FileReadSettings settings,
        CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var totalLines = 0;
        var linesReturned = 0;
        var truncated = false;
        int? nextStartLine = null;
        var stopCollecting = false;

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var lineIndex = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (settings.CountTotalLines)
            {
                totalLines++;
            }

            if (!stopCollecting
                && lineIndex + 1 >= selection.StartLine
                && lineIndex + 1 <= selection.EndLine)
            {
                var formatted = FormatLine(lineIndex + 1, line, settings.MaxLineChars);
                var prefix = linesReturned == 0 ? string.Empty : Environment.NewLine;
                var addition = prefix + formatted;
                if (content.Length + addition.Length > settings.MaxResponseChars)
                {
                    truncated = true;
                    nextStartLine = lineIndex + 1;
                    stopCollecting = true;
                }
                else
                {
                    content.Append(addition);
                    linesReturned++;
                }
            }

            if (!settings.CountTotalLines
                && lineIndex + 1 >= selection.EndLine
                && linesReturned >= selection.EndLine - selection.StartLine + 1)
            {
                totalLines = lineIndex + 1;
                if (await reader.ReadLineAsync(cancellationToken) is not null)
                {
                    truncated = true;
                    nextStartLine = selection.StartLine + linesReturned;
                }

                break;
            }

            lineIndex++;
        }

        if (settings.CountTotalLines
            && !truncated
            && linesReturned >= selection.EndLine - selection.StartLine + 1
            && totalLines >= selection.StartLine + linesReturned)
        {
            truncated = true;
            nextStartLine = selection.StartLine + linesReturned;
        }

        if (!settings.CountTotalLines && totalLines == 0)
        {
            totalLines = lineIndex;
        }

        var body = content.ToString();
        if (linesReturned > 0 || truncated || selection.StartLine > 1)
        {
            body = AppendMetaFooter(body, totalLines, linesReturned, selection.StartLine, truncated, nextStartLine);
        }

        return new ReadResult(body, totalLines, linesReturned, selection.StartLine, truncated, nextStartLine);
    }

    internal static ReadResult ReadFromText(string text, Selection selection, FileReadSettings settings)
    {
        var content = new StringBuilder();
        var totalLines = 0;
        var linesReturned = 0;
        var truncated = false;
        int? nextStartLine = null;
        var stopCollecting = false;
        var lineIndex = 0;

        using var reader = new StringReader(text ?? string.Empty);
        while (true)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (settings.CountTotalLines)
            {
                totalLines++;
            }

            if (!stopCollecting
                && lineIndex + 1 >= selection.StartLine
                && lineIndex + 1 <= selection.EndLine)
            {
                var formatted = FormatLine(lineIndex + 1, line, settings.MaxLineChars);
                var prefix = linesReturned == 0 ? string.Empty : Environment.NewLine;
                var addition = prefix + formatted;
                if (content.Length + addition.Length > settings.MaxResponseChars)
                {
                    truncated = true;
                    nextStartLine = lineIndex + 1;
                    stopCollecting = true;
                }
                else
                {
                    content.Append(addition);
                    linesReturned++;
                }
            }

            if (!settings.CountTotalLines
                && lineIndex + 1 >= selection.EndLine
                && linesReturned >= selection.EndLine - selection.StartLine + 1)
            {
                totalLines = lineIndex + 1;
                if (reader.ReadLine() is not null)
                {
                    truncated = true;
                    nextStartLine = selection.StartLine + linesReturned;
                }

                break;
            }

            lineIndex++;
        }

        if (settings.CountTotalLines
            && !truncated
            && linesReturned >= selection.EndLine - selection.StartLine + 1
            && totalLines >= selection.StartLine + linesReturned)
        {
            truncated = true;
            nextStartLine = selection.StartLine + linesReturned;
        }

        if (!settings.CountTotalLines && totalLines == 0)
        {
            totalLines = lineIndex;
        }

        var body = content.ToString();
        if (linesReturned > 0 || truncated || selection.StartLine > 1)
        {
            body = AppendMetaFooter(body, totalLines, linesReturned, selection.StartLine, truncated, nextStartLine);
        }

        return new ReadResult(body, totalLines, linesReturned, selection.StartLine, truncated, nextStartLine);
    }

    internal static string FormatLine(int lineNumber, string line, int maxLineChars)
    {
        if (line.Length > maxLineChars)
        {
            line = line[..maxLineChars] + LineTruncatedSuffix;
        }

        return $"{lineNumber}|{line}";
    }

    internal static string AppendMetaFooter(
        string body,
        int totalLines,
        int linesReturned,
        int startLine,
        bool truncated,
        int? nextStartLine)
    {
        var builder = new StringBuilder();
        if (body.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.AppendLine(MetaHeader);
        builder.AppendLine($"total_lines: {totalLines}");
        builder.AppendLine($"lines_returned: {linesReturned}");
        builder.AppendLine($"start_line: {startLine}");
        builder.AppendLine($"end_line: {Math.Max(startLine - 1, startLine + linesReturned - 1)}");
        builder.AppendLine($"truncated: {truncated.ToString().ToLowerInvariant()}");
        if (truncated && nextStartLine is not null)
        {
            builder.AppendLine($"next_start_line: {nextStartLine.Value}");
        }

        return body + builder.ToString().TrimEnd();
    }

    internal static string BuildSummary(string fileName, ReadResult result)
    {
        var summary = $"Read {result.LinesReturned} of {result.TotalLines} lines from {fileName}";
        if (result.Truncated && result.NextStartLine is not null)
        {
            summary += $"; truncated — continue with start_line={result.NextStartLine.Value}";
        }

        return summary;
    }
}
