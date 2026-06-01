using System.Text;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal static class FileReadLineReader
{
    internal const string LineTruncatedSuffix = "... [line truncated]";
    internal const string MetaHeader = "--- file_read meta ---";

    internal sealed record Selection(int Offset, int LineLimit);

    internal sealed record ReadResult(
        string Body,
        int TotalLines,
        int LinesReturned,
        int Offset,
        bool Truncated,
        int? NextOffset);

    internal static Selection ResolveSelection(ToolInvocation invocation, FileReadSettings settings)
    {
        var offset = ToolArguments.GetInt32(invocation, "offset", 0);
        var limit = ToolArguments.GetInt32(invocation, "limit", 0);
        var startLine = ToolArguments.GetInt32(invocation, "start_line", 0);
        var endLine = ToolArguments.GetInt32(invocation, "end_line", 0);

        if (startLine > 0)
        {
            offset = Math.Max(0, startLine - 1);
            limit = endLine >= startLine ? endLine - startLine + 1 : 0;
        }

        if (limit <= 0)
        {
            limit = settings.DefaultLineLimit;
        }

        limit = Math.Min(limit, settings.MaxLinesPerCall);
        offset = Math.Max(0, offset);
        return new Selection(offset, limit);
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
        int? nextOffset = null;
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
                && lineIndex >= selection.Offset
                && linesReturned < selection.LineLimit)
            {
                var formatted = FormatLine(lineIndex + 1, line, settings.MaxLineChars);
                var prefix = linesReturned == 0 ? string.Empty : Environment.NewLine;
                var addition = prefix + formatted;
                if (content.Length + addition.Length > settings.MaxResponseChars)
                {
                    truncated = true;
                    nextOffset = lineIndex;
                    stopCollecting = true;
                }
                else
                {
                    content.Append(addition);
                    linesReturned++;
                }
            }

            if (!settings.CountTotalLines
                && lineIndex >= selection.Offset + selection.LineLimit - 1
                && linesReturned >= selection.LineLimit)
            {
                totalLines = lineIndex + 1;
                if (await reader.ReadLineAsync(cancellationToken) is not null)
                {
                    truncated = true;
                    nextOffset = selection.Offset + linesReturned;
                }

                break;
            }

            lineIndex++;
        }

        if (settings.CountTotalLines
            && !truncated
            && linesReturned >= selection.LineLimit
            && totalLines > selection.Offset + linesReturned)
        {
            truncated = true;
            nextOffset = selection.Offset + linesReturned;
        }

        if (!settings.CountTotalLines && totalLines == 0)
        {
            totalLines = lineIndex;
        }

        var body = content.ToString();
        if (linesReturned > 0 || truncated || selection.Offset > 0)
        {
            body = AppendMetaFooter(body, totalLines, linesReturned, selection.Offset, truncated, nextOffset);
        }

        return new ReadResult(body, totalLines, linesReturned, selection.Offset, truncated, nextOffset);
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
        int offset,
        bool truncated,
        int? nextOffset)
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
        builder.AppendLine($"offset: {offset}");
        builder.AppendLine($"truncated: {truncated.ToString().ToLowerInvariant()}");
        if (truncated && nextOffset is not null)
        {
            builder.AppendLine($"next_offset: {nextOffset.Value}");
        }

        return body + builder.ToString().TrimEnd();
    }

    internal static string BuildSummary(string fileName, ReadResult result)
    {
        var summary = $"Read {result.LinesReturned} of {result.TotalLines} lines from {fileName}";
        if (result.Truncated && result.NextOffset is not null)
        {
            summary += $"; truncated — continue with offset={result.NextOffset.Value}";
        }

        return summary;
    }
}
