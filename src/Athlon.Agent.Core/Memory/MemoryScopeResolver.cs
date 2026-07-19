using System.Security.Cryptography;
using System.Text;

namespace Athlon.Agent.Core.Memory;

/// <summary>
/// Resolves the on-disk memory scope: AppData/memory/projects/{workspaceKey}/sessions/{sessionId}/.
/// </summary>
public static class MemoryScopeResolver
{
    public const string ProjectsFolderName = "projects";
    public const string SessionsFolderName = "sessions";

    public static bool TryResolve(
        IActiveWorkspaceContext workspace,
        IActiveAgentSessionContext session,
        out string workspaceKey,
        out string sessionId)
    {
        workspaceKey = string.Empty;
        sessionId = session.SessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (!TryResolveWorkspaceKey(workspace, out workspaceKey))
        {
            return false;
        }

        return true;
    }

    public static bool TryResolveWorkspaceKey(IActiveWorkspaceContext workspace, out string workspaceKey)
    {
        workspaceKey = string.Empty;
        if (!string.IsNullOrWhiteSpace(workspace.WorkspaceId))
        {
            workspaceKey = SanitizeSegment(workspace.WorkspaceId);
            return true;
        }

        if (string.IsNullOrWhiteSpace(workspace.RootPath))
        {
            return false;
        }

        workspaceKey = HashPath(workspace.RootPath);
        return true;
    }

    public static string BuildMemoryDir(string appRoot, string memoryDirName, string workspaceKey, string sessionId) =>
        Path.Combine(
            appRoot,
            memoryDirName,
            ProjectsFolderName,
            SanitizeSegment(workspaceKey),
            SessionsFolderName,
            SanitizeSegment(sessionId));

    public static string SanitizeSegment(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "_";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    public static string HashPath(string rootPath)
    {
        var normalized = rootPath.Replace('\\', '/').Trim().TrimEnd('/').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
