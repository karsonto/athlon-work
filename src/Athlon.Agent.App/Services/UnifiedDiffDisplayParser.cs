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

public sealed record DiffDisplayLine(DiffLineKind Kind, string Text, int? CollapsedCount = null);

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
        foreach (var line in SplitLines(unifiedDiff))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
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
                raw.Add(new DiffDisplayLine(DiffLineKind.Added, line[1..]));
                continue;
            }

            if (line.StartsWith('-'))
            {
                raw.Add(new DiffDisplayLine(DiffLineKind.Removed, line[1..]));
                continue;
            }

            if (line.StartsWith(' '))
            {
                raw.Add(new DiffDisplayLine(DiffLineKind.Context, line[1..]));
                continue;
            }

            // Bare context / misc lines (no prefix)
            if (!string.IsNullOrEmpty(line))
            {
                raw.Add(new DiffDisplayLine(DiffLineKind.Context, line));
            }
        }

        return foldContext ? FoldContextRuns(raw) : raw;
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
