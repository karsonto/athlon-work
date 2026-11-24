using System.IO;

namespace Athlon.Agent.App.Services.Room;

/// <summary>
/// Maps host workspace/skills absolute paths ↔ container paths (/workspace, /skills).
/// Outward-facing tool results keep host paths.
/// </summary>
public sealed class RoomSandboxPathMap
{
    public const string ContainerWorkspaceRoot = "/workspace";
    public const string ContainerSkillsRoot = "/skills";

    public RoomSandboxPathMap(string hostWorkspaceRoot, string hostSkillsRoot)
    {
        HostWorkspaceRoot = NormalizeHostRoot(hostWorkspaceRoot)
            ?? throw new ArgumentException("Workspace root is required.", nameof(hostWorkspaceRoot));
        HostSkillsRoot = NormalizeHostRoot(hostSkillsRoot)
            ?? throw new ArgumentException("Skills root is required.", nameof(hostSkillsRoot));
    }

    public string HostWorkspaceRoot { get; }

    public string HostSkillsRoot { get; }

    public static string? NormalizeHostRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Host absolute path → container absolute path, or null if outside mounts.</summary>
    public string? ToContainer(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(hostPath.Trim());
        }
        catch
        {
            return null;
        }

        if (TryMapUnder(full, HostWorkspaceRoot, ContainerWorkspaceRoot, out var mapped)
            || TryMapUnder(full, HostSkillsRoot, ContainerSkillsRoot, out mapped))
        {
            return mapped;
        }

        return null;
    }

    /// <summary>Container path → host absolute path, or null if outside mounts.</summary>
    public string? ToHost(string containerPath)
    {
        if (string.IsNullOrWhiteSpace(containerPath))
        {
            return null;
        }

        var unix = containerPath.Trim().Replace('\\', '/');
        if (!unix.StartsWith('/'))
        {
            unix = "/" + unix;
        }

        if (TryUnmap(unix, ContainerWorkspaceRoot, HostWorkspaceRoot, out var host)
            || TryUnmap(unix, ContainerSkillsRoot, HostSkillsRoot, out host))
        {
            return host;
        }

        return null;
    }

    public bool IsUnderMount(string hostPath) => ToContainer(hostPath) is not null;

    private static bool TryMapUnder(string fullHost, string hostRoot, string containerRoot, out string mapped)
    {
        mapped = string.Empty;
        if (!IsUnderRoot(fullHost, hostRoot))
        {
            return false;
        }

        var relative = Path.GetRelativePath(hostRoot, fullHost).Replace('\\', '/');
        if (relative is "." or "")
        {
            mapped = containerRoot;
            return true;
        }

        mapped = containerRoot + "/" + relative.TrimStart('/');
        return true;
    }

    private static bool TryUnmap(string containerPath, string containerRoot, string hostRoot, out string host)
    {
        host = string.Empty;
        if (!IsUnderUnixRoot(containerPath, containerRoot))
        {
            return false;
        }

        var relative = containerPath.Length == containerRoot.Length
            ? string.Empty
            : containerPath[(containerRoot.Length)..].TrimStart('/');
        host = string.IsNullOrEmpty(relative)
            ? hostRoot
            : Path.GetFullPath(Path.Combine(hostRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        return true;
    }

    private static bool IsUnderRoot(string fullPath, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(fullPath, root, comparison))
        {
            return true;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, comparison);
    }

    private static bool IsUnderUnixRoot(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        return path.StartsWith(root + "/", StringComparison.Ordinal);
    }
}
