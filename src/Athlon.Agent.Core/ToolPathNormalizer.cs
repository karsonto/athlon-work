namespace Athlon.Agent.Core;

/// <summary>
/// Validates and normalizes file paths from model tool arguments before file I/O.
/// Workspace-relative paths use forward slashes (/); Windows file APIs accept them.
/// </summary>
public static class ToolPathNormalizer
{
    public const string PathArgumentName = "path";

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

    public static IReadOnlyDictionary<string, string> NormalizePathArguments(IReadOnlyDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue(PathArgumentName, out var path) || string.IsNullOrWhiteSpace(path))
        {
            return arguments;
        }

        if (!TryNormalizeForFileOperation(path, out var normalized, out _))
        {
            return arguments;
        }

        if (string.Equals(path, normalized, StringComparison.Ordinal))
        {
            return arguments;
        }

        var copy = new Dictionary<string, string>(arguments, StringComparer.OrdinalIgnoreCase);
        copy[PathArgumentName] = normalized;
        return copy;
    }
}
