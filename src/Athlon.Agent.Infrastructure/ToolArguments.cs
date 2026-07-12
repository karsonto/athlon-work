using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public static class ToolArguments
{
    public static bool TryGetRequired(ToolInvocation invocation, string name, out string value, out ToolResult error)
    {
        if (invocation.Arguments.TryGetString(name, out value!) && !string.IsNullOrWhiteSpace(value))
        {
            error = ToolResult.Success("OK");
            return true;
        }

        error = ToolResult.Failure("Missing argument", $"{invocation.ToolName} requires `{name}`.");
        return false;
    }

    public static int GetInt32(ToolInvocation invocation, string name, int defaultValue) =>
        invocation.Arguments.GetInt32(name, defaultValue);

    public static bool TryGetNormalizedPath(ToolInvocation invocation, out string path, out ToolResult error)
    {
        if (!TryGetRequired(invocation, ToolPathNormalizer.PathArgumentName, out var raw, out error))
        {
            path = string.Empty;
            return false;
        }

        if (!ToolPathNormalizer.TryNormalizeForFileOperation(raw, out path, out var message))
        {
            path = string.Empty;
            error = ToolResult.Failure("Invalid path", $"{invocation.ToolName}: {message}");
            return false;
        }

        error = ToolResult.Success("OK");
        return true;
    }

    public static bool TryGetOptionalNormalizedPath(
        ToolInvocation invocation,
        out string path,
        out ToolResult error,
        string defaultPath = ".")
    {
        if (!invocation.Arguments.TryGetString(ToolPathNormalizer.PathArgumentName, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            path = ToolPathNormalizer.ForModel(defaultPath);
            error = ToolResult.Success("OK");
            return true;
        }

        if (!ToolPathNormalizer.TryNormalizeForFileOperation(raw, out path, out var message))
        {
            path = string.Empty;
            error = ToolResult.Failure("Invalid path", $"{invocation.ToolName}: {message}");
            return false;
        }

        error = ToolResult.Success("OK");
        return true;
    }

    /// <summary>Resolves execute_command cwd: workspace root by default, workspace-relative when set.</summary>
    public static bool TryResolveWorkingDirectory(
        ToolInvocation invocation,
        WorkspaceGuard guard,
        out string fullPath,
        out ToolResult error)
    {
        if (guard.HasConfiguredWorkspace)
        {
            try
            {
                if (!invocation.Arguments.TryGetString(ToolPathNormalizer.CwdArgumentName, out var raw)
                    || string.IsNullOrWhiteSpace(raw))
                {
                    fullPath = guard.Normalize(".");
                    error = ToolResult.Success("OK");
                    return true;
                }

                if (!ToolPathNormalizer.TryNormalizeForFileOperation(raw, out var normalized, out var message))
                {
                    fullPath = string.Empty;
                    error = ToolResult.Failure("Invalid working directory", $"{invocation.ToolName}: {message}");
                    return false;
                }

                fullPath = guard.Normalize(normalized);
                if (!guard.IsInsideWorkspace(fullPath))
                {
                    error = ToolResult.Failure("Outside workspace", fullPath);
                    fullPath = string.Empty;
                    return false;
                }

                if (!Directory.Exists(fullPath))
                {
                    error = ToolResult.Failure(
                        "Invalid working directory",
                        $"Working directory does not exist: {ToolPathNormalizer.ForModel(normalized)}");
                    fullPath = string.Empty;
                    return false;
                }

                error = ToolResult.Success("OK");
                return true;
            }
            catch (InvalidOperationException ex)
            {
                fullPath = string.Empty;
                error = ToolResult.Failure("Workspace not configured", ex.Message);
                return false;
            }
        }

        var cwd = invocation.Arguments.GetString(ToolPathNormalizer.CwdArgumentName);
        if (string.IsNullOrWhiteSpace(cwd))
        {
            fullPath = Environment.CurrentDirectory;
            error = ToolResult.Success("OK");
            return true;
        }

        if (!ToolPathNormalizer.TryNormalizeForFileOperation(cwd, out var normalizedCwd, out var cwdMessage))
        {
            fullPath = string.Empty;
            error = ToolResult.Failure("Invalid working directory", $"{invocation.ToolName}: {cwdMessage}");
            return false;
        }

        var rooted = Path.GetFullPath(normalizedCwd.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(rooted))
        {
            error = ToolResult.Failure(
                "Invalid working directory",
                $"Working directory does not exist: {normalizedCwd}");
            fullPath = string.Empty;
            return false;
        }

        fullPath = rooted;
        error = ToolResult.Success("OK");
        return true;
    }
}
