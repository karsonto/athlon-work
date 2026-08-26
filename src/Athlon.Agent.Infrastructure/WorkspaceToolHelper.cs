using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal static class WorkspaceToolHelper
{
    public static bool TryResolveNormalizedPath(
        ToolInvocation invocation,
        WorkspaceGuard guard,
        out string fullPath,
        out ToolResult error,
        bool requireInsideWorkspace = true)
    {
        if (!ToolArguments.TryGetNormalizedPath(invocation, out var path, out error))
        {
            fullPath = string.Empty;
            return false;
        }

        fullPath = guard.Normalize(path);
        if (!requireInsideWorkspace)
        {
            error = ToolResult.Success("OK");
            return true;
        }

        return TryEnsureInsideWorkspace(guard, ref fullPath, out error);
    }

    public static bool TryResolveOptionalNormalizedPath(
        ToolInvocation invocation,
        WorkspaceGuard guard,
        out string fullPath,
        out ToolResult error,
        string defaultPath = ".",
        bool requireInsideWorkspace = true)
    {
        if (!ToolArguments.TryGetOptionalNormalizedPath(invocation, out var path, out error, defaultPath))
        {
            fullPath = string.Empty;
            return false;
        }

        fullPath = guard.Normalize(path);
        if (!requireInsideWorkspace)
        {
            error = ToolResult.Success("OK");
            return true;
        }

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

    public static string ToAuditPath(WorkspaceGuard guard, string fullPath)
    {
        try
        {
            var root = guard.Normalize(".");
            return ToolPathNormalizer.ForModel(Path.GetRelativePath(root, fullPath));
        }
        catch
        {
            return ToolPathNormalizer.ForModel(fullPath);
        }
    }

    public static Task AuditAsync(
        AuditLogService audit,
        string toolName,
        object payload,
        CancellationToken cancellationToken) =>
        audit.WriteAsync(toolName, payload, cancellationToken);
}
