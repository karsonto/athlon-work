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

/// <summary>
/// Cursor V4A patches (<c>*** Begin Patch</c>) and the shared apply step.
/// Unified diffs are converted into the same <see cref="PatchFile"/> shape.
/// </summary>
internal static class PatchDocument
{
    private const string BeginMarker = "*** Begin Patch";
    private const string EndMarker = "*** End Patch";
    private const string AddMarker = "*** Add File:";
    private const string DeleteMarker = "*** Delete File:";
    private const string UpdateMarker = "*** Update File:";
    private const string MoveMarker = "*** Move to:";
    private const string EofMarker = "*** End of File";

    public static bool TryParseV4A(string patch, out IReadOnlyList<PatchFile> files, out string? error)
    {
        files = Array.Empty<PatchFile>();
        var lines = TextLineEndings.NormalizeToLf(patch).Split('\n');
        var parsed = new List<PatchFile>();
        string? path = null;
        PatchFileKind kind = PatchFileKind.Update;
        string? moveTo = null;
        var chunks = new List<PatchChunk>();
        ChunkBuilder? current = null;
        var sawBegin = false;
        var sawEnd = false;
        var omitTrailingNewline = false;

        void FlushChunk()
        {
            if (current is null)
            {
                return;
            }

            if (current.Lines.Count > 0 || current.EndOfFile)
            {
                chunks.Add(new PatchChunk(
                    current.Anchor,
                    current.Lines.ToArray(),
                    current.EndOfFile,
                    SearchStart: 0,
                    RetryFromBeginning: false));
            }

            current = null;
        }

        void FlushFile()
        {
            FlushChunk();
            if (path is null)
            {
                return;
            }

            parsed.Add(new PatchFile(kind, path, moveTo, chunks.ToArray(), omitTrailingNewline));
            path = null;
            moveTo = null;
            kind = PatchFileKind.Update;
            chunks = [];
            omitTrailingNewline = false;
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                if (rawLine.Length > 0 && current is not null)
                {
                    current.Lines.Add(new UnifiedDiffLine(UnifiedDiffLineKind.Context, string.Empty));
                }

                continue;
            }

            if (trimmed == BeginMarker)
            {
                sawBegin = true;
                continue;
            }

            if (trimmed == EndMarker)
            {
                FlushFile();
                sawEnd = true;
                break;
            }

            if (TryTake(trimmed, UpdateMarker, out var updatePath))
            {
                FlushFile();
                kind = PatchFileKind.Update;
                path = NormalizePath(updatePath);
                continue;
            }

            if (TryTake(trimmed, AddMarker, out var addPath))
            {
                FlushFile();
                kind = PatchFileKind.Add;
                path = NormalizePath(addPath);
                continue;
            }

            if (TryTake(trimmed, DeleteMarker, out var deletePath))
            {
                FlushFile();
                kind = PatchFileKind.Delete;
                path = NormalizePath(deletePath);
                continue;
            }

            if (TryTake(trimmed, MoveMarker, out var destination))
            {
                if (path is null || kind != PatchFileKind.Update)
                {
                    error = "*** Move to must follow *** Update File.";
                    return false;
                }

                moveTo = NormalizePath(destination);
                continue;
            }

            if (trimmed == EofMarker)
            {
                current ??= new ChunkBuilder();
                current.EndOfFile = true;
                continue;
            }

            if (trimmed.StartsWith("@@", StringComparison.Ordinal))
            {
                FlushChunk();
                current = new ChunkBuilder { Anchor = ReadAnchor(trimmed) };
                continue;
            }

            if (trimmed == "\\ No newline at end of file")
            {
                omitTrailingNewline = true;
                continue;
            }

            if (path is null)
            {
                continue;
            }

            if (kind == PatchFileKind.Delete)
            {
                error = $"{path}: delete file does not take hunk lines.";
                return false;
            }

            var prefix = rawLine[0];
            if (prefix is not (' ' or '+' or '-'))
            {
                error = $"Invalid patch line: {trimmed}";
                return false;
            }

            current ??= new ChunkBuilder();
            var text = rawLine.Length == 1 ? string.Empty : rawLine[1..];
            var lineKind = prefix switch
            {
                '+' => UnifiedDiffLineKind.Add,
                '-' => UnifiedDiffLineKind.Remove,
                _ => UnifiedDiffLineKind.Context
            };
            current.Lines.Add(new UnifiedDiffLine(lineKind, text));
        }

        if (!sawBegin)
        {
            error = "Patch is missing *** Begin Patch.";
            return false;
        }

        if (!sawEnd)
        {
            FlushFile();
            error = "Patch is missing *** End Patch.";
            return false;
        }

        if (parsed.Count == 0)
        {
            error = "No file hunks found. Start with *** Begin Patch and *** Update File, *** Add File, or *** Delete File.";
            return false;
        }

        foreach (var file in parsed)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                error = "Patch file path is empty.";
                return false;
            }

            if (file.Kind == PatchFileKind.Update
                && file.MoveTo is null
                && file.Chunks.Count == 0)
            {
                error = $"{file.Path}: patch has file headers but no changes.";
                return false;
            }

            if (file.Kind == PatchFileKind.Add && file.Chunks.Count == 0)
            {
                error = $"{file.Path}: add file has no lines.";
                return false;
            }
        }

        files = parsed;
        error = null;
        return true;
    }

    public static PatchFile FromUnified(UnifiedDiffFile file)
    {
        var omit = file.Hunks.Any(hunk => hunk.OmitTrailingNewline);
        if (string.Equals(file.NewPath, "/dev/null", StringComparison.OrdinalIgnoreCase))
        {
            return new PatchFile(PatchFileKind.Delete, file.OldPath ?? file.NewPath, null, [], omit);
        }

        var chunks = file.Hunks.Select(hunk => new PatchChunk(
            Anchor: null,
            hunk.Lines,
            EndOfFile: false,
            SearchStart: Math.Max(0, hunk.OldStart - 1),
            RetryFromBeginning: true)).ToArray();

        if (file.IsNewFile)
        {
            return new PatchFile(PatchFileKind.Add, file.NewPath, null, chunks, omit);
        }

        var oldPath = file.OldPath ?? file.NewPath;
        var moved = !string.Equals(oldPath, file.NewPath, StringComparison.OrdinalIgnoreCase);
        return new PatchFile(
            PatchFileKind.Update,
            moved ? oldPath : file.NewPath,
            moved ? file.NewPath : null,
            chunks,
            omit);
    }

    private static bool TryTake(string trimmed, string marker, out string rest)
    {
        if (!trimmed.StartsWith(marker, StringComparison.Ordinal))
        {
            rest = string.Empty;
            return false;
        }

        rest = trimmed[marker.Length..].Trim().Trim('"');
        return true;
    }

    private static string? ReadAnchor(string trimmed)
    {
        if (trimmed.Length <= 2)
        {
            return null;
        }

        var rest = trimmed[2..].TrimStart();
        return rest.Length == 0 ? null : rest;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim();

    private sealed class ChunkBuilder
    {
        public string? Anchor { get; init; }

        public List<UnifiedDiffLine> Lines { get; } = [];

        public bool EndOfFile { get; set; }
    }
}

internal static class PatchApplier
{
    private enum LineMatch
    {
        Exact,
        TrimEnd,
        Trim,
        Normalized
    }

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
                var anchorAt = Seek(fileLines, [chunk.Anchor], start, eof: false, out _);
                if (anchorAt is null && chunk.RetryFromBeginning && start > 0)
                {
                    anchorAt = Seek(fileLines, [chunk.Anchor], 0, eof: false, out _);
                }

                if (anchorAt is null)
                {
                    return PatchApplyResult.Fail($"Failed to find context '{chunk.Anchor}'");
                }

                start = anchorAt.Value + 1;
            }

            if (chunk.OldLines.Count == 0)
            {
                replacements.Add(new Replacement(fileLines.Count, 0, chunk.NewLines.ToArray(), LineMatch.Exact));
                continue;
            }

            var found = Seek(fileLines, chunk.OldLines, start, chunk.EndOfFile, out var kind);
            if (found is null && chunk.RetryFromBeginning && start > 0 && !chunk.EndOfFile)
            {
                found = Seek(fileLines, chunk.OldLines, 0, eof: false, out kind);
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

            var inserted = kind == LineMatch.Exact
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

        var found = Seek(fileLines, chunk.NewLines, start, chunk.EndOfFile, out _);
        if (found is null && chunk.RetryFromBeginning && start > 0 && !chunk.EndOfFile)
        {
            found = Seek(fileLines, chunk.NewLines, 0, eof: false, out _);
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

    private static int? Seek(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> pattern,
        int start,
        bool eof,
        out LineMatch kind)
    {
        kind = LineMatch.Exact;
        if (pattern.Count == 0)
        {
            return Math.Clamp(start, 0, lines.Count);
        }

        if (pattern.Count > lines.Count)
        {
            return null;
        }

        var last = lines.Count - pattern.Count;
        var searchStart = eof ? last : start;
        if (searchStart < 0 || searchStart > last)
        {
            return null;
        }

        if (TryPass(lines, pattern, searchStart, last, LineEqualsExact))
        {
            kind = LineMatch.Exact;
            return Find(lines, pattern, searchStart, last, LineEqualsExact);
        }

        if (TryPass(lines, pattern, searchStart, last, LineEqualsTrimEnd))
        {
            kind = LineMatch.TrimEnd;
            return Find(lines, pattern, searchStart, last, LineEqualsTrimEnd);
        }

        if (TryPass(lines, pattern, searchStart, last, LineEqualsTrim))
        {
            kind = LineMatch.Trim;
            return Find(lines, pattern, searchStart, last, LineEqualsTrim);
        }

        if (TryPass(lines, pattern, searchStart, last, LineEqualsNormalized))
        {
            kind = LineMatch.Normalized;
            return Find(lines, pattern, searchStart, last, LineEqualsNormalized);
        }

        return null;
    }

    private static bool TryPass(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> pattern,
        int searchStart,
        int last,
        Func<string, string, bool> equals) =>
        Find(lines, pattern, searchStart, last, equals) is not null;

    private static int? Find(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> pattern,
        int searchStart,
        int last,
        Func<string, string, bool> equals)
    {
        for (var i = searchStart; i <= last; i++)
        {
            var matched = true;
            for (var j = 0; j < pattern.Count; j++)
            {
                if (!equals(lines[i + j], pattern[j]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return null;
    }

    private static bool LineEqualsExact(string fileLine, string pattern) =>
        string.Equals(fileLine, pattern, StringComparison.Ordinal);

    private static bool LineEqualsTrimEnd(string fileLine, string pattern) =>
        string.Equals(fileLine.TrimEnd(), pattern.TrimEnd(), StringComparison.Ordinal);

    private static bool LineEqualsTrim(string fileLine, string pattern) =>
        string.Equals(fileLine.Trim(), pattern.Trim(), StringComparison.Ordinal);

    private static bool LineEqualsNormalized(string fileLine, string pattern) =>
        string.Equals(Normalize(fileLine), Normalize(pattern), StringComparison.Ordinal);

    private static string Normalize(string text)
    {
        var trimmed = text.Trim();
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            builder.Append(c switch
            {
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
                '\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
                '\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',
                '\u00A0' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or '\u2006'
                    or '\u2007' or '\u2008' or '\u2009' or '\u200A' or '\u202F' or '\u205F' or '\u3000' => ' ',
                _ => c
            });
        }

        return builder.ToString();
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

    private readonly record struct Replacement(int Start, int OldLength, IReadOnlyList<string> Lines, LineMatch Kind);
}
