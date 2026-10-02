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
    IReadOnlyList<UnifiedDiffLine> Lines,
    bool OmitTrailingNewline = false);

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

        if (patch.Contains("*** Begin Patch", StringComparison.Ordinal)
            && !patch.Contains("--- ", StringComparison.Ordinal))
        {
            error = "Only unified diff is supported (--- / +++ / @@). The *** Begin Patch format is not supported.";
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
        var omitTrailingNewline = false;
        string? lineError = null;

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
                currentHunkLines,
                omitTrailingNewline));
            currentHunkLines = null;
            oldStart = null;
            oldCount = null;
            newStart = null;
            newCount = null;
            omitTrailingNewline = false;
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

            if (rawLine.Length == 0)
            {
                continue;
            }

            if (currentHunkLines is not null && IsGitMetadata(rawLine))
            {
                FlushHunk();
                continue;
            }

            if (currentHunkLines is null)
            {
                continue;
            }

            if (rawLine.StartsWith("\\", StringComparison.Ordinal))
            {
                omitTrailingNewline = true;
                continue;
            }

            var prefix = rawLine[0];
            var text = rawLine.Length == 1 ? string.Empty : rawLine[1..];
            switch (prefix)
            {
                case ' ':
                    currentHunkLines.Add(new UnifiedDiffLine(UnifiedDiffLineKind.Context, text));
                    break;
                case '-':
                    currentHunkLines.Add(new UnifiedDiffLine(UnifiedDiffLineKind.Remove, text));
                    break;
                case '+':
                    currentHunkLines.Add(new UnifiedDiffLine(UnifiedDiffLineKind.Add, text));
                    break;
                default:
                    lineError = $"Invalid hunk line prefix '{prefix}' in patch.";
                    break;
            }

            if (lineError is not null)
            {
                break;
            }
        }

        if (lineError is not null)
        {
            error = lineError;
            return false;
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

    private static bool IsGitMetadata(string line) =>
        line.StartsWith("diff --git ", StringComparison.Ordinal)
        || line.StartsWith("index ", StringComparison.Ordinal)
        || line.StartsWith("old mode ", StringComparison.Ordinal)
        || line.StartsWith("new mode ", StringComparison.Ordinal)
        || line.StartsWith("new file mode ", StringComparison.Ordinal)
        || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
        || line.StartsWith("similarity index ", StringComparison.Ordinal)
        || line.StartsWith("dissimilarity index ", StringComparison.Ordinal)
        || line.StartsWith("rename from ", StringComparison.Ordinal)
        || line.StartsWith("rename to ", StringComparison.Ordinal)
        || line.StartsWith("copy from ", StringComparison.Ordinal)
        || line.StartsWith("copy to ", StringComparison.Ordinal);

    private static string NormalizeDiffPath(string path)
    {
        path = path.Trim();
        var tab = path.IndexOf('\t');
        if (tab >= 0)
        {
            path = path[..tab].Trim();
        }

        path = path.Trim('"');
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
        var hadTrailingNewline = !hunk.OmitTrailingNewline
            && (content.EndsWith('\n') || content.EndsWith("\r\n", StringComparison.Ordinal));
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

    /// <summary>
    /// Restores the original file's newline style. Hunks are applied on LF text; a CRLF
    /// source must be written back as CRLF so the patch does not rewrite the whole file.
    /// </summary>
    public static string ToDiskText(string original, string patchedLf) =>
        original.Contains("\r\n", StringComparison.Ordinal)
            ? patchedLf.Replace("\n", "\r\n", StringComparison.Ordinal)
            : patchedLf;
}
