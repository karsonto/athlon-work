using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal static class WorkspaceToolHelper
{
    public static bool TryResolveNormalizedPath(
        ToolInvocation invocation,
        WorkspaceGuard guard,
        out string fullPath,
        out ToolResult error)
    {
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
        out string fullPath,
        out ToolResult error,
        string defaultPath = ".")
    {
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

    public static Task AuditAsync(
        AuditLogService audit,
        string toolName,
        object payload,
        CancellationToken cancellationToken) =>
        audit.WriteAsync(toolName, payload, cancellationToken);
}
