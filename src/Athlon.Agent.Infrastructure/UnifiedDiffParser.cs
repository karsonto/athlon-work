using System.Text;
using System.Text.RegularExpressions;

namespace Athlon.Agent.Infrastructure;

internal enum UnifiedDiffLineKind
{
    Context,
    Remove,
    Add
}

internal readonly record struct UnifiedDiffLine(UnifiedDiffLineKind Kind, string Text);

internal sealed record UnifiedDiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<UnifiedDiffLine> Lines);

internal sealed record UnifiedDiffFile(string? OldPath, string NewPath, IReadOnlyList<UnifiedDiffHunk> Hunks)
{
    public bool IsNewFile => string.Equals(OldPath, "/dev/null", StringComparison.OrdinalIgnoreCase);
}

internal static partial class UnifiedDiffParser
{
    private static readonly Regex HunkHeaderPattern = HunkHeaderRegex();

    public static bool TryParse(string patch, out IReadOnlyList<UnifiedDiffFile> files, out string? error)
    {
        files = Array.Empty<UnifiedDiffFile>();
        if (string.IsNullOrWhiteSpace(patch))
        {
            error = "Patch text is empty.";
            return false;
        }

        var normalized = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var parsedFiles = new List<UnifiedDiffFile>();
        string? oldPath = null;
        string? newPath = null;
        var hunks = new List<UnifiedDiffHunk>();
        List<UnifiedDiffLine>? currentHunkLines = null;
        int? oldStart = null;
        int? oldCount = null;
        int? newStart = null;
        int? newCount = null;

        void FlushHunk()
        {
            if (currentHunkLines is null)
            {
                return;
            }

            hunks.Add(new UnifiedDiffHunk(
                oldStart ?? 1,
                oldCount ?? 0,
                newStart ?? 1,
                newCount ?? 0,
                currentHunkLines));
            currentHunkLines = null;
            oldStart = null;
            oldCount = null;
            newStart = null;
            newCount = null;
        }

        void FlushFile()
        {
            FlushHunk();
            if (newPath is not null)
            {
                parsedFiles.Add(new UnifiedDiffFile(oldPath, newPath, hunks.ToArray()));
            }

            oldPath = null;
            newPath = null;
            hunks.Clear();
        }

        foreach (var rawLine in lines)
        {
            if (rawLine.StartsWith("--- ", StringComparison.Ordinal))
            {
                FlushFile();
                oldPath = NormalizeDiffPath(rawLine[4..].Trim());
                continue;
            }

            if (rawLine.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = NormalizeDiffPath(rawLine[4..].Trim());
                continue;
            }

            var hunkMatch = HunkHeaderPattern.Match(rawLine);
            if (hunkMatch.Success)
            {
                FlushHunk();
                oldStart = int.Parse(hunkMatch.Groups[1].Value);
                oldCount = hunkMatch.Groups[2].Success ? int.Parse(hunkMatch.Groups[2].Value) : 1;
                newStart = int.Parse(hunkMatch.Groups[3].Value);
                newCount = hunkMatch.Groups[4].Success ? int.Parse(hunkMatch.Groups[4].Value) : 1;
                currentHunkLines = [];
                continue;
            }

            if (currentHunkLines is null)
            {
                continue;
            }

            if (rawLine.Length == 0)
            {
                currentHunkLines.Add(new UnifiedDiffLine(UnifiedDiffLineKind.Context, string.Empty));
                continue;
            }

            var prefix = rawLine[0];
            var text = rawLine[1..];
            currentHunkLines.Add(prefix switch
            {
                ' ' => new UnifiedDiffLine(UnifiedDiffLineKind.Context, text),
                '-' => new UnifiedDiffLine(UnifiedDiffLineKind.Remove, text),
                '+' => new UnifiedDiffLine(UnifiedDiffLineKind.Add, text),
                _ => throw new FormatException($"Invalid hunk line prefix '{prefix}' in patch.")
            });
        }

        FlushFile();

        if (parsedFiles.Count == 0)
        {
            error = "No file hunks found. Patch must use unified diff format (--- / +++ / @@).";
            return false;
        }

        files = parsedFiles;
        error = null;
        return true;
    }

    private static string NormalizeDiffPath(string path)
    {
        path = path.Trim().Trim('"');
        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path.Replace('\\', '/');
    }

    [GeneratedRegex(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@")]
    private static partial Regex HunkHeaderRegex();
}

internal static class UnifiedDiffApplier
{
    public static bool TryApply(string content, UnifiedDiffHunk hunk, out string patched, out string? error)
    {
        var hadTrailingNewline = content.EndsWith('\n') || content.EndsWith("\r\n", StringComparison.Ordinal);
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var fileLines = normalized.Length == 0 ? new List<string>() : normalized.Split('\n').ToList();
        if (fileLines.Count > 0 && fileLines[^1] == string.Empty && normalized.EndsWith('\n'))
        {
            fileLines.RemoveAt(fileLines.Count - 1);
        }

        var index = Math.Max(0, hunk.OldStart - 1);
        var cursor = index;

        foreach (var line in hunk.Lines)
        {
            switch (line.Kind)
            {
                case UnifiedDiffLineKind.Context:
                case UnifiedDiffLineKind.Remove:
                    if (cursor >= fileLines.Count || !string.Equals(fileLines[cursor], line.Text, StringComparison.Ordinal))
                    {
                        patched = content;
                        error = $"Hunk context mismatch at line {cursor + 1}. Expected: {line.Text}";
                        return false;
                    }

                    cursor++;
                    break;
                case UnifiedDiffLineKind.Add:
                    break;
            }
        }

        var replacement = new List<string>();
        foreach (var line in hunk.Lines)
        {
            if (line.Kind is UnifiedDiffLineKind.Context or UnifiedDiffLineKind.Add)
            {
                replacement.Add(line.Text);
            }
        }

        fileLines.RemoveRange(index, cursor - index);
        fileLines.InsertRange(index, replacement);

        var joined = string.Join('\n', fileLines);
        if (hadTrailingNewline && joined.Length > 0)
        {
            joined += '\n';
        }

        patched = joined;
        error = null;
        return true;
    }

    public static string ApplyAllHunks(string content, IReadOnlyList<UnifiedDiffHunk> hunks, out string? error)
    {
        var current = content;
        foreach (var hunk in hunks.OrderByDescending(h => h.OldStart))
        {
            if (!TryApply(current, hunk, out current, out error))
            {
                return content;
            }
        }

        error = null;
        return current;
    }
}
