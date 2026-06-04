namespace Athlon.Agent.Core;

/// <summary>
/// Validates and normalizes file paths from model tool arguments before file I/O.
/// Workspace-relative paths use forward slashes (/); Windows file APIs accept them.
/// </summary>
public static class ToolPathNormalizer
{
    public const string PathArgumentName = "path";
    public const string CwdArgumentName = "cwd";

    public static string ForModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return path.Replace('\\', '/').Trim();
    }

    public static bool TryNormalizeForFileOperation(string? path, out string normalized, out string errorMessage)
    {
        normalized = string.Empty;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            errorMessage = "Path cannot be empty.";
            return false;
        }

        var trimmed = path.Trim();
        if (trimmed.IndexOf('\0') >= 0)
        {
            errorMessage = "Path contains invalid characters.";
            return false;
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Path must be a workspace file path, not a URI.";
            return false;
        }

        normalized = ForModel(trimmed);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            errorMessage = "Path cannot be empty.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Strips mistaken workspace-root or workspace-folder prefixes from model-supplied paths.
    /// Rare edge case: if the workspace root folder is named e.g. "src" and the project also
    /// has a top-level src/ directory, a legitimate path like src/utils/foo.cs is stripped to utils/foo.cs.
    /// </summary>
    public static string ResolveRelativeToWorkspaceRoot(string path, string workspaceRoot)
    {
        path = ForModel(path);
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return path;
        }

        var normalizedRoot = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootForward = normalizedRoot.Replace('\\', '/');
        var pathForFullPath = path.Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(pathForFullPath))
        {
            var full = Path.GetFullPath(pathForFullPath);
            if (full.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return ".";
            }

            var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
            if (full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return ForModel(Path.GetRelativePath(normalizedRoot, full));
            }

            return path;
        }

        if (path.Equals(rootForward, StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }

        if (path.StartsWith(rootForward + "/", StringComparison.OrdinalIgnoreCase))
        {
            return path[(rootForward.Length + 1)..];
        }

        var folderName = Path.GetFileName(normalizedRoot);
        if (string.IsNullOrEmpty(folderName))
        {
            return path;
        }

        if (path.Equals(folderName, StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }

        if (path.StartsWith(folderName + "/", StringComparison.OrdinalIgnoreCase))
        {
            return path[(folderName.Length + 1)..];
        }

        return path;
    }

    public static IReadOnlyDictionary<string, string> NormalizePathArguments(IReadOnlyDictionary<string, string> arguments)
    {
        Dictionary<string, string>? copy = null;

        if (TryNormalizeArgument(arguments, PathArgumentName, out var normalizedPath))
        {
            copy ??= new Dictionary<string, string>(arguments, StringComparer.OrdinalIgnoreCase);
            copy[PathArgumentName] = normalizedPath;
        }

        if (TryNormalizeArgument(arguments, CwdArgumentName, out var normalizedCwd))
        {
            copy ??= new Dictionary<string, string>(arguments, StringComparer.OrdinalIgnoreCase);
            copy[CwdArgumentName] = normalizedCwd;
        }

        return copy ?? arguments;
    }

    private static bool TryNormalizeArgument(
        IReadOnlyDictionary<string, string> arguments,
        string name,
        out string normalized)
    {
        normalized = string.Empty;
        if (!arguments.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!TryNormalizeForFileOperation(raw, out normalized, out _))
        {
            return false;
        }

        return !string.Equals(raw, normalized, StringComparison.Ordinal);
    }
}
