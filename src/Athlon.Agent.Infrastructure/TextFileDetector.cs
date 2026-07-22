namespace Athlon.Agent.Infrastructure;

public static class TextFileDetector
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".props", ".targets", ".xaml", ".xml", ".json", ".jsonc",
        ".md", ".markdown", ".txt", ".log", ".csv", ".tsv",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
        ".py", ".rb", ".go", ".rs", ".java", ".kt", ".kts",
        ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".m", ".mm",
        ".sql", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd",
        ".html", ".htm", ".css", ".scss", ".less", ".svg",
        ".dockerfile", ".gitignore", ".gitattributes", ".editorconfig",
        ".env", ".env.example", ".cshtml", ".razor", ".vue", ".svelte",
        ".fs", ".fsx", ".vb", ".swift", ".php", ".lua", ".r", ".scala",
        ".gradle", ".properties", ".resx", ".axaml", ".pen",
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dockerfile", "makefile", "cmakelists.txt", "license", "readme", ".gitignore",
    };

    public static bool IsTextFile(string path, bool skipLocalDirectoryCheck = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!skipLocalDirectoryCheck && Directory.Exists(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (TextFileNames.Contains(fileName))
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(extension) && TextExtensions.Contains(extension))
        {
            return true;
        }

        if (string.IsNullOrEmpty(extension))
        {
            return TextFileNames.Contains(fileName.ToLowerInvariant());
        }

        return false;
    }

    public static bool ContainsBinaryContent(ReadOnlySpan<byte> sample)
    {
        foreach (var b in sample)
        {
            if (b == 0)
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<bool> LooksBinaryOnDiskAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        var buffer = new byte[Math.Min(8192, stream.Length > 0 ? (int)Math.Min(stream.Length, 8192) : 8192)];
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
        return ContainsBinaryContent(buffer.AsSpan(0, read));
    }
}
