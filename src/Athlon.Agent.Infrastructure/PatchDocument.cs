namespace Athlon.Agent.Infrastructure;

internal enum PatchFileKind
{
    Update,
    Add,
    Delete
}

internal enum PatchApplyStatus
{
    Changed,
    AlreadyApplied,
    Unchanged
}

internal sealed record PatchChunk(
    string? Anchor,
    IReadOnlyList<UnifiedDiffLine> Lines,
    bool EndOfFile,
    int SearchStart,
    bool RetryFromBeginning)
{
    public IReadOnlyList<string> OldLines { get; } = Lines
        .Where(line => line.Kind is UnifiedDiffLineKind.Context or UnifiedDiffLineKind.Remove)
        .Select(line => line.Text)
        .ToArray();

    public IReadOnlyList<string> NewLines { get; } = Lines
        .Where(line => line.Kind is UnifiedDiffLineKind.Context or UnifiedDiffLineKind.Add)
        .Select(line => line.Text)
        .ToArray();
}

internal sealed record PatchFile(
    PatchFileKind Kind,
    string Path,
    string? MoveTo,
    IReadOnlyList<PatchChunk> Chunks,
    bool OmitTrailingNewline)
{
    public int HunkCount => Chunks.Count;
}

internal readonly record struct PatchApplyResult(PatchApplyStatus Status, string Text, string? Error)
{
    public bool Succeeded => Error is null;

    public static PatchApplyResult Fail(string error) =>
        new(PatchApplyStatus.Unchanged, string.Empty, error);
}

internal static class PatchApplier
{
    public static PatchApplyResult Apply(string content, PatchFile file)
    {
        var normalized = TextLineEndings.NormalizeToLf(content);
        var hadTrailingNewline = normalized.EndsWith('\n') || normalized.EndsWith('\r');
        var fileLines = Split(normalized);
        var replacements = new List<Replacement>();
        var alreadyApplied = 0;
        var lineIndex = 0;

        foreach (var chunk in file.Chunks)
        {
            var start = chunk.RetryFromBeginning ? chunk.SearchStart : lineIndex;
            if (!string.IsNullOrEmpty(chunk.Anchor))
            {
                var anchorAt = PatchLineMatcher.Seek(fileLines, [chunk.Anchor], start, eof: false, out _);
                if (anchorAt is null && chunk.RetryFromBeginning && start > 0)
                {
                    anchorAt = PatchLineMatcher.Seek(fileLines, [chunk.Anchor], 0, eof: false, out _);
                }

                if (anchorAt is null)
                {
                    return PatchApplyResult.Fail($"Failed to find context '{chunk.Anchor}'");
                }

                start = anchorAt.Value + 1;
            }

            if (chunk.OldLines.Count == 0)
            {
                replacements.Add(new Replacement(fileLines.Count, 0, chunk.NewLines.ToArray(), PatchLineMatcher.LineMatch.Exact));
                continue;
            }

            var found = PatchLineMatcher.Seek(fileLines, chunk.OldLines, start, chunk.EndOfFile, out var kind);
            if (found is null && chunk.RetryFromBeginning && start > 0 && !chunk.EndOfFile)
            {
                found = PatchLineMatcher.Seek(fileLines, chunk.OldLines, 0, eof: false, out kind);
            }

            if (found is null)
            {
                if (IsAlreadyApplied(fileLines, chunk, start))
                {
                    alreadyApplied++;
                    continue;
                }

                return PatchApplyResult.Fail(FormatMissing(chunk.OldLines));
            }

            var inserted = kind == PatchLineMatcher.LineMatch.Exact
                ? chunk.NewLines.ToArray()
                : RebuildKeepingFileContext(fileLines, found.Value, chunk);
            replacements.Add(new Replacement(found.Value, chunk.OldLines.Count, inserted, kind));
            if (!chunk.RetryFromBeginning)
            {
                lineIndex = found.Value + chunk.OldLines.Count;
            }
        }

        if (replacements.Count == 0)
        {
            return alreadyApplied > 0
                ? new PatchApplyResult(PatchApplyStatus.AlreadyApplied, content, null)
                : new PatchApplyResult(PatchApplyStatus.Unchanged, content, null);
        }

        foreach (var replacement in replacements.OrderByDescending(item => item.Start))
        {
            fileLines.RemoveRange(replacement.Start, Math.Min(replacement.OldLength, fileLines.Count - replacement.Start));
            fileLines.InsertRange(replacement.Start, replacement.Lines);
        }

        var trailing = !file.OmitTrailingNewline && (hadTrailingNewline || file.Kind == PatchFileKind.Add);
        var patched = Join(fileLines, trailing);
        if (string.Equals(patched, normalized, StringComparison.Ordinal))
        {
            return new PatchApplyResult(PatchApplyStatus.Unchanged, content, null);
        }

        return new PatchApplyResult(PatchApplyStatus.Changed, patched, null);
    }

    private static bool IsAlreadyApplied(IReadOnlyList<string> fileLines, PatchChunk chunk, int start)
    {
        if (chunk.NewLines.Count == 0)
        {
            return true;
        }

        var found = PatchLineMatcher.Seek(fileLines, chunk.NewLines, start, chunk.EndOfFile, out _);
        if (found is null && chunk.RetryFromBeginning && start > 0 && !chunk.EndOfFile)
        {
            found = PatchLineMatcher.Seek(fileLines, chunk.NewLines, 0, eof: false, out _);
        }

        return found is not null;
    }

    private static string[] RebuildKeepingFileContext(IReadOnlyList<string> fileLines, int start, PatchChunk chunk)
    {
        var rebuilt = new List<string>();
        var oldOffset = 0;
        foreach (var line in chunk.Lines)
        {
            switch (line.Kind)
            {
                case UnifiedDiffLineKind.Context:
                    rebuilt.Add(fileLines[start + oldOffset]);
                    oldOffset++;
                    break;
                case UnifiedDiffLineKind.Remove:
                    oldOffset++;
                    break;
                case UnifiedDiffLineKind.Add:
                    rebuilt.Add(line.Text);
                    break;
            }
        }

        return rebuilt.ToArray();
    }

    private static string FormatMissing(IReadOnlyList<string> lines)
    {
        var shown = lines.Take(12).Select(line => line.Length > 200 ? line[..200] : line);
        var text = string.Join('\n', shown);
        if (lines.Count > 12)
        {
            text += "\n...";
        }

        return "Failed to find expected lines:\n" + text;
    }

    private static List<string> Split(string normalized)
    {
        if (normalized.Length == 0)
        {
            return [];
        }

        var lines = normalized.Split('\n').ToList();
        if (lines[^1].Length == 0 && normalized.EndsWith('\n'))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string Join(IReadOnlyList<string> lines, bool trailingNewline)
    {
        var text = string.Join('\n', lines);
        if (trailingNewline && text.Length > 0)
        {
            text += '\n';
        }

        return text;
    }

    private readonly record struct Replacement(int Start, int OldLength, IReadOnlyList<string> Lines, PatchLineMatcher.LineMatch Kind);
}
