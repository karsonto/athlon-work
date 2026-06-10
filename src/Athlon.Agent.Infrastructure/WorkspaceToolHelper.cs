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
        return true;
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
        return true;
    }

    public static Task AuditAsync(
        AuditLogService audit,
        string toolName,
        object payload,
        CancellationToken cancellationToken) =>
        audit.WriteAsync(toolName, payload, cancellationToken);
}
