namespace Athlon.Agent.Core;

/// <summary>
/// Path helpers used across the model-facing (forward-slash) and native (separator)
/// path conventions. Previously the same few normalization expressions were inlined
/// in ToolPathNormalizer, MemoryScopeResolver, WorkspaceSessionBridge, and several
/// App view models. Centralizing here removes drift and keeps one definition.
/// </summary>
public static class PathUtil
{
    /// <summary>Converts backslashes to forward slashes and trims surrounding whitespace.</summary>
    public static string ToForwardSlashes(string path) => path.Replace('\\', '/').Trim();

    /// <summary>Strips trailing directory separators (both slash styles) from a path.</summary>
    public static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Extracts the final path segment (folder/file name) after normalizing trailing
    /// separators. Equivalent to <c>Path.GetFileName(TrimTrailingSeparators(path))</c>.
    /// </summary>
    public static string DirectoryName(string path) =>
        Path.GetFileName(TrimTrailingSeparators(path));
}
