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

/// <summary>Parses unified diffs into display rows (with optional context folding).</summary>
public static class UnifiedDiffDisplayParser
{
    private const int CollapseContextThreshold = 3;

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

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
