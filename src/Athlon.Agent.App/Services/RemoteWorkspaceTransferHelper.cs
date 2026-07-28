using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

/// <summary>Pure helpers for SSH workspace download/upload UI.</summary>
public static class RemoteWorkspaceTransferHelper
{
    /// <summary>
    /// Resolves the remote directory that should receive a file drop.
    /// Directory node → itself; file node → parent; null → workspace root.
    /// </summary>
    public static string ResolveDropTargetDirectory(
        string workspaceRoot,
        string? hitNodeFullPath,
        bool hitNodeIsDirectory)
    {
        var root = RemotePathNormalizer.NormalizeRoot(workspaceRoot);
        if (string.IsNullOrWhiteSpace(hitNodeFullPath))
        {
            return root.Length == 0 ? "/" : root;
        }

        var path = RemotePathNormalizer.Collapse(hitNodeFullPath);
        if (hitNodeIsDirectory)
        {
            return path;
        }

        return RemotePathNormalizer.GetDirectoryName(path) ?? root;
    }

    public static bool IsRemoteTargetAllowed(string workspaceRoot, string remoteDirectory) =>
        RemotePathNormalizer.IsUnderRoot(remoteDirectory, workspaceRoot);

    /// <summary>
    /// Builds relative remote path segments under <paramref name="remoteRoot"/> for a remote file.
    /// </summary>
    public static string ToRelativeRemotePath(string remoteRoot, string remoteFilePath)
    {
        var root = RemotePathNormalizer.NormalizeRoot(remoteRoot).TrimEnd('/');
        var file = RemotePathNormalizer.Collapse(remoteFilePath);
        if (root.Length == 0 || root == "/")
        {
            return file.TrimStart('/');
        }

        if (string.Equals(file, root, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var prefix = root + "/";
        return file.StartsWith(prefix, StringComparison.Ordinal)
            ? file[prefix.Length..]
            : RemotePathNormalizer.GetFileName(file);
    }
}
