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

    public static bool TryParse(string patch, out IReadOnlyList<PatchFile> files, out string? error)
    {
        files = Array.Empty<PatchFile>();
        if (string.IsNullOrWhiteSpace(patch))
        {
            error = "Patch text is empty.";
            return false;
        }

        patch = patch.TrimStart('\uFEFF');
        if (patch.Contains("*** Begin Patch", StringComparison.Ordinal))
        {
            return PatchDocument.TryParseV4A(patch, out files, out error);
        }

        if (!TryParseUnified(patch, out var unified, out error))
        {
            return false;
        }

        files = unified.Select(PatchDocument.FromUnified).ToArray();
        return true;
    }

    private static bool TryParseUnified(string patch, out IReadOnlyList<UnifiedDiffFile> files, out string? error)
    {
        files = Array.Empty<UnifiedDiffFile>();
        var normalized = TextLineEndings.NormalizeToLf(patch);
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

            var hunkMatch = HunkHeaderPattern.Match(NormalizeHunkHeader(rawLine));
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

            if (currentHunkLines is null && rawLine.TrimStart().StartsWith("@@", StringComparison.Ordinal))
            {
                lineError = $"Unrecognized hunk header: {rawLine.Trim()}";
                break;
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

        var withoutHunks = parsedFiles.FirstOrDefault(file => file.Hunks.Count == 0);
        if (withoutHunks is not null)
        {
            error = $"{withoutHunks.NewPath}: patch has file headers but no @@ hunks. "
                + "Each change needs a hunk header such as @@ -12,3 +12,4 @@ and lines starting with a space, '-' or '+'.";
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

    /// <summary>
    /// Models often omit the spaces around <c>@@</c> or emit a unicode minus in the ranges.
    /// Those headers used to be skipped, leaving a file with zero hunks that was reported as a no-op.
    /// </summary>
    private static string NormalizeHunkHeader(string line)
    {
        if (!line.Contains("@@", StringComparison.Ordinal))
        {
            return line;
        }

        return line
            .Replace('\u2212', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\uFE63', '-')
            .Replace('\uFF0D', '-');
    }

    [GeneratedRegex(@"^@@\s*-(\d+)(?:,\s*(\d+))?\s+\+(\d+)(?:,\s*(\d+))?\s*@@")]
    private static partial Regex HunkHeaderRegex();
}
