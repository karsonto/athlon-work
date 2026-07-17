using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.Ssh;

internal static class SshWorkspaceToolHelper
{
    public static bool TryEnsureConnected(ISshWorkspaceClient client, out ToolResult error)
    {
        if (client.IsConnected)
        {
            error = ToolResult.Success("OK");
            return true;
        }

        error = ToolResult.Failure("SSH not connected", "Remote workspace is not connected. Reconfigure the SSH workspace and try again.");
        return false;
    }

    public static bool TryResolveNormalizedPath(
        ToolInvocation invocation,
        WorkspaceGuard guard,
        ISshWorkspaceClient client,
        out string fullPath,
        out ToolResult error)
    {
        if (!TryEnsureConnected(client, out error))
        {
            fullPath = string.Empty;
            return false;
        }

        if (!ToolArguments.TryGetNormalizedPath(invocation, out var path, out error))
        {
            fullPath = string.Empty;
            return false;
        }

        fullPath = guard.Normalize(path);
        return TryEnsureInsideWorkspace(guard, ref fullPath, out error);
    }

    public static bool TryResolveOptionalNormalizedPath(
        ToolInvocation invocation,
        WorkspaceGuard guard,
        ISshWorkspaceClient client,
        out string fullPath,
        out ToolResult error,
        string defaultPath = ".")
    {
        if (!TryEnsureConnected(client, out error))
        {
            fullPath = string.Empty;
            return false;
        }

        if (!ToolArguments.TryGetOptionalNormalizedPath(invocation, out var path, out error, defaultPath))
        {
            fullPath = string.Empty;
            return false;
        }

        fullPath = guard.Normalize(path);
        return TryEnsureInsideWorkspace(guard, ref fullPath, out error);
    }

    private static bool TryEnsureInsideWorkspace(WorkspaceGuard guard, ref string fullPath, out ToolResult error)
    {
        if (guard.HasConfiguredWorkspace && !guard.IsInsideWorkspace(fullPath))
        {
            error = ToolResult.Failure("Outside workspace", fullPath);
            fullPath = string.Empty;
            return false;
        }

        error = ToolResult.Success("OK");
        return true;
    }

    public static string ToRelative(string workspaceRoot, string fullPath)
    {
        var root = RemotePathNormalizer.NormalizeRoot(workspaceRoot);
        var path = RemotePathNormalizer.Collapse(fullPath);
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return ".";
        }

        if (root != "/" && path.StartsWith(root + "/", StringComparison.Ordinal))
        {
            return path[(root.Length + 1)..];
        }

        return path.TrimStart('/');
    }

    public static bool ShouldIgnore(string fullPath, IReadOnlyList<string> ignorePatterns)
    {
        var segments = RemotePathNormalizer.ForModel(fullPath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => WorkspacePathFilter.ShouldIgnoreEntryName(segment, ignorePatterns));
    }
}
