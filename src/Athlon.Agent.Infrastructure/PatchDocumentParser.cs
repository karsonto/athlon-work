namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Cursor V4A patches (<c>*** Begin Patch</c>) and the shared apply step.
/// Unified diffs are converted into the same <see cref="PatchFile"/> shape.
/// </summary>
internal static class PatchDocumentParser
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
