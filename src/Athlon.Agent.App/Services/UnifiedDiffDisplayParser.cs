namespace Athlon.Agent.App.Services;

public enum DiffLineKind
{
    Header,
    HunkHeader,
    Added,
    Removed,
    Context,
    Collapsed
}

public sealed record DiffDisplayLine(
    DiffLineKind Kind,
    string Text,
    int? CollapsedCount = null,
    int? OldLine = null,
    int? NewLine = null);

public readonly record struct DiffChangeCounts(int Added, int Removed);

/// <summary>One row in the stacked review diff. Collapsed rows may keep the hidden context lines.</summary>
public sealed record DiffReviewLine(
    DiffLineKind Kind,
    string Text,
    int? LineNumber = null,
    int? CollapsedCount = null,
    bool CanExpand = false,
    IReadOnlyList<DiffReviewLine>? HiddenLines = null);

/// <summary>Review rows plus the last new-file line number, used to count a trailing unmodified gap.</summary>
public sealed record DiffReviewDocument(IReadOnlyList<DiffReviewLine> Lines, int? LastNewLine);

/// <summary>Parses unified diffs into display rows (with optional context folding).</summary>
public static class UnifiedDiffDisplayParser
{
    private const int CollapseContextThreshold = 3;

    /// <summary>Context lines kept on each side of a change in <see cref="BuildReview"/>.</summary>
    public const int ReviewContextMargin = 3;

    public static DiffChangeCounts CountChanges(string? unifiedDiff)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return default;
        }

        var added = 0;
        var removed = 0;
        foreach (var line in SplitLines(unifiedDiff))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('+'))
            {
                added++;
            }
            else if (line.StartsWith('-'))
            {
                removed++;
            }
        }

        return new DiffChangeCounts(added, removed);
    }

    public static IReadOnlyList<DiffDisplayLine> Parse(string? unifiedDiff, bool foldContext = true)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return Array.Empty<DiffDisplayLine>();
        }

        var raw = new List<DiffDisplayLine>();
        int? oldLine = null;
        int? newLine = null;
        foreach (var line in SplitLines(unifiedDiff))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (TryReadHunkStarts(line, out var oldStart, out var newStart))
                {
                    oldLine = oldStart;
                    newLine = newStart;
                }

                raw.Add(new DiffDisplayLine(DiffLineKind.HunkHeader, line));
                continue;
            }

            if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal))
            {
                raw.Add(new DiffDisplayLine(DiffLineKind.Header, line));
                continue;
            }

            if (line.StartsWith('+'))
            {
                raw.Add(new DiffDisplayLine(DiffLineKind.Added, line[1..], NewLine: newLine));
                if (newLine is not null)
                {
                    newLine++;
                }

                continue;
            }

            if (line.StartsWith('-'))
            {
                raw.Add(new DiffDisplayLine(DiffLineKind.Removed, line[1..], OldLine: oldLine));
                if (oldLine is not null)
                {
                    oldLine++;
                }

                continue;
            }

            if (line.StartsWith(' '))
            {
                raw.Add(new DiffDisplayLine(DiffLineKind.Context, line[1..], OldLine: oldLine, NewLine: newLine));
                if (oldLine is not null)
                {
                    oldLine++;
                }

                if (newLine is not null)
                {
                    newLine++;
                }

                continue;
            }

            // Bare context / misc lines (no prefix)
            if (!string.IsNullOrEmpty(line))
            {
                raw.Add(new DiffDisplayLine(DiffLineKind.Context, line, OldLine: oldLine, NewLine: newLine));
                if (oldLine is not null)
                {
                    oldLine++;
                }

                if (newLine is not null)
                {
                    newLine++;
                }
            }
        }

        return foldContext ? FoldContextRuns(raw) : raw;
    }

    /// <summary>
    /// Builds Cursor-style review rows: no file or hunk headers, three lines of context beside each change,
    /// expandable folds for context that is inside the patch, and non-expandable folds for gaps the patch skips.
    /// </summary>
    public static DiffReviewDocument BuildReview(string? unifiedDiff)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return new DiffReviewDocument(Array.Empty<DiffReviewLine>(), null);
        }

        var hunks = new List<(int? NewStart, List<DiffDisplayLine> Body)>();
        int? pendingStart = null;
        List<DiffDisplayLine>? pendingBody = null;
        foreach (var line in Parse(unifiedDiff, foldContext: false))
        {
            if (line.Kind is DiffLineKind.Header)
            {
                continue;
            }

            if (line.Kind is DiffLineKind.HunkHeader)
            {
                if (pendingBody is not null)
                {
                    hunks.Add((pendingStart, pendingBody));
                }

                pendingStart = TryReadHunkStarts(line.Text, out _, out var newStart) ? newStart : null;
                pendingBody = new List<DiffDisplayLine>();
                continue;
            }

            if (pendingBody is null)
            {
                continue;
            }

            if (line.Kind is DiffLineKind.Added or DiffLineKind.Removed or DiffLineKind.Context)
            {
                pendingBody.Add(line);
            }
        }

        if (pendingBody is not null)
        {
            hunks.Add((pendingStart, pendingBody));
        }

        if (hunks.Count == 0)
        {
            return new DiffReviewDocument(Array.Empty<DiffReviewLine>(), null);
        }

        var lines = new List<DiffReviewLine>();
        var covered = 0;
        int? lastNew = null;
        foreach (var (newStart, body) in hunks)
        {
            if (newStart is int start)
            {
                var gap = start - covered - 1;
                if (gap > 0)
                {
                    lines.Add(new DiffReviewLine(DiffLineKind.Collapsed, string.Empty, CollapsedCount: gap));
                }
            }

            lines.AddRange(FoldHunk(body));
            if (LastNewInBody(body) is int bodyLast)
            {
                covered = bodyLast;
            }
            else if (newStart is int hunkStart)
            {
                covered = Math.Max(0, hunkStart - 1);
            }

            lastNew = covered;
        }

        return new DiffReviewDocument(lines, lastNew);
    }

    private static bool TryReadHunkStarts(string line, out int oldStart, out int newStart)
    {
        oldStart = 0;
        newStart = 0;
        var minus = line.IndexOf('-');
        var plus = line.IndexOf('+');
        if (minus < 0 || plus < 0 || plus < minus)
        {
            return false;
        }

        return TryReadLeadingInt(line, minus + 1, out oldStart)
            && TryReadLeadingInt(line, plus + 1, out newStart);
    }

    private static bool TryReadLeadingInt(string text, int start, out int value)
    {
        value = 0;
        var end = start;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        return end > start && int.TryParse(text.AsSpan(start, end - start), out value);
    }

    private static IReadOnlyList<DiffDisplayLine> FoldContextRuns(IReadOnlyList<DiffDisplayLine> lines)
    {
        var result = new List<DiffDisplayLine>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Kind != DiffLineKind.Context)
            {
                result.Add(lines[i]);
                i++;
                continue;
            }

            var start = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Context)
            {
                i++;
            }

            var count = i - start;
            if (count >= CollapseContextThreshold)
            {
                result.Add(new DiffDisplayLine(DiffLineKind.Collapsed, string.Empty, count));
            }
            else
            {
                for (var j = start; j < i; j++)
                {
                    result.Add(lines[j]);
                }
            }
        }

        return result;
    }

    private static int? LastNewInBody(IReadOnlyList<DiffDisplayLine> body)
    {
        int? last = null;
        foreach (var line in body)
        {
            if (line.NewLine is int number)
            {
                last = number;
            }
        }

        return last;
    }

    private static IReadOnlyList<DiffReviewLine> FoldHunk(IReadOnlyList<DiffDisplayLine> body)
    {
        var segments = new List<(bool IsContext, List<DiffDisplayLine> Lines)>();
        foreach (var line in body)
        {
            var isContext = line.Kind == DiffLineKind.Context;
            if (segments.Count == 0 || segments[^1].IsContext != isContext)
            {
                segments.Add((isContext, new List<DiffDisplayLine>()));
            }

            segments[^1].Lines.Add(line);
        }

        var hasChange = false;
        foreach (var segment in segments)
        {
            if (!segment.IsContext)
            {
                hasChange = true;
                break;
            }
        }

        var result = new List<DiffReviewLine>();
        for (var i = 0; i < segments.Count; i++)
        {
            var (isContext, segmentLines) = segments[i];
            if (!isContext)
            {
                foreach (var line in segmentLines)
                {
                    result.Add(ToCodeLine(line));
                }

                continue;
            }

            var leading = i == 0;
            var trailing = i == segments.Count - 1;
            if (!hasChange || (leading && trailing))
            {
                AppendMiddleContext(result, segmentLines);
            }
            else if (leading)
            {
                AppendKeepTail(result, segmentLines);
            }
            else if (trailing)
            {
                AppendKeepHead(result, segmentLines);
            }
            else
            {
                AppendMiddleContext(result, segmentLines);
            }
        }

        return result;
    }

    private static void AppendKeepTail(List<DiffReviewLine> result, IReadOnlyList<DiffDisplayLine> lines)
    {
        if (lines.Count <= ReviewContextMargin)
        {
            AddCodeLines(result, lines);
            return;
        }

        var hiddenCount = lines.Count - ReviewContextMargin;
        result.Add(ToCollapsed(Take(lines, 0, hiddenCount)));
        AddCodeLines(result, Take(lines, hiddenCount, lines.Count - hiddenCount));
    }

    private static void AppendKeepHead(List<DiffReviewLine> result, IReadOnlyList<DiffDisplayLine> lines)
    {
        if (lines.Count <= ReviewContextMargin)
        {
            AddCodeLines(result, lines);
            return;
        }

        AddCodeLines(result, Take(lines, 0, ReviewContextMargin));
        result.Add(ToCollapsed(Take(lines, ReviewContextMargin, lines.Count - ReviewContextMargin)));
    }

    private static void AppendMiddleContext(List<DiffReviewLine> result, IReadOnlyList<DiffDisplayLine> lines)
    {
        if (lines.Count <= ReviewContextMargin * 2)
        {
            AddCodeLines(result, lines);
            return;
        }

        AddCodeLines(result, Take(lines, 0, ReviewContextMargin));
        var hiddenStart = ReviewContextMargin;
        var hiddenCount = lines.Count - (ReviewContextMargin * 2);
        result.Add(ToCollapsed(Take(lines, hiddenStart, hiddenCount)));
        AddCodeLines(result, Take(lines, hiddenStart + hiddenCount, ReviewContextMargin));
    }

    private static void AddCodeLines(List<DiffReviewLine> result, IReadOnlyList<DiffDisplayLine> lines)
    {
        foreach (var line in lines)
        {
            result.Add(ToCodeLine(line));
        }
    }

    private static List<DiffDisplayLine> Take(IReadOnlyList<DiffDisplayLine> lines, int start, int count)
    {
        var slice = new List<DiffDisplayLine>(count);
        for (var i = 0; i < count; i++)
        {
            slice.Add(lines[start + i]);
        }

        return slice;
    }

    private static DiffReviewLine ToCodeLine(DiffDisplayLine line)
    {
        var number = line.Kind == DiffLineKind.Removed ? line.OldLine : line.NewLine;
        return new DiffReviewLine(line.Kind, line.Text, number);
    }

    private static DiffReviewLine ToCollapsed(IReadOnlyList<DiffDisplayLine> hidden)
    {
        var hiddenLines = new DiffReviewLine[hidden.Count];
        for (var i = 0; i < hidden.Count; i++)
        {
            hiddenLines[i] = ToCodeLine(hidden[i]);
        }

        return new DiffReviewLine(
            DiffLineKind.Collapsed,
            string.Empty,
            CollapsedCount: hidden.Count,
            CanExpand: true,
            HiddenLines: hiddenLines);
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
