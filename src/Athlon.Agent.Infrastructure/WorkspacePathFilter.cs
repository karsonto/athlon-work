namespace Athlon.Agent.Infrastructure;

public static class WorkspacePathFilter
{
    /// <summary>True when any path segment matches a configured ignore directory name.</summary>
    public static bool ShouldIgnorePath(string fullPath, IReadOnlyList<string> directoryNames)
    {
        foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (ShouldIgnoreEntryName(segment, directoryNames))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ShouldIgnoreEntryName(string name, IReadOnlyList<string> directoryNames) =>
        directoryNames.Any(pattern => string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase));
}
