using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class FileWriteTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "file_write",
        "Create or overwrite a file. Use empty string for content to create a zero-byte file.",
        new Dictionary<string, string>
        {
            ["path"] = ToolPathDescriptions.WorkspaceRelativePath,
            ["content"] = "New content (empty string allowed)"
        },
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceToolHelper.TryResolveNormalizedPath(invocation, guard, out var fullPath, out var error))
        {
            return EnrichFailure(invocation, error);
        }

        if (!TryGetContent(invocation, out var content, out error))
        {
            return error;
        }

        var modelPath = invocation.Arguments.GetValueOrDefault(ToolPathNormalizer.PathArgumentName) ?? fullPath;

        try
        {
            if (Directory.Exists(fullPath))
            {
                return ToolResult.Failure(
                    "Path is a directory",
                    $"Cannot write file: '{ToolPathNormalizer.ForModel(modelPath)}' is a directory.");
            }

            var parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent))
            {
                return ToolResult.Failure(
                    "Invalid path",
                    $"Cannot determine parent directory for '{ToolPathNormalizer.ForModel(modelPath)}'.");
            }

            Directory.CreateDirectory(parent);
            await AtomicFile.WriteAllTextAsync(fullPath, content, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Failure("Write failed", DescribeWriteException(ex, modelPath));
        }

        await WorkspaceToolHelper.AuditAsync(
            audit,
            "file_write",
            new { path = fullPath, chars = content.Length },
            cancellationToken);

        var fileName = Path.GetFileName(fullPath);
        var summary = content.Length == 0
            ? $"Created empty file {fileName}"
            : $"Wrote {content.Length} chars to {fileName}";
        return ToolResult.Success(summary);
    }

    private static bool TryGetContent(ToolInvocation invocation, out string content, out ToolResult error)
    {
        if (!invocation.Arguments.TryGetValue("content", out content!))
        {
            content = string.Empty;
            error = ToolResult.Failure(
                "Missing argument",
                "file_write requires `content`. Pass \"\" (empty string) to create a zero-byte file.");
            return false;
        }

        content ??= string.Empty;
        error = ToolResult.Success("OK");
        return true;
    }

    private static ToolResult EnrichFailure(ToolInvocation invocation, ToolResult error)
    {
        if (error.Succeeded)
        {
            return error;
        }

        var modelPath = invocation.Arguments.GetValueOrDefault(ToolPathNormalizer.PathArgumentName);
        return error.Summary switch
        {
            "Outside workspace" =>
                ToolResult.Failure(
                    error.Summary,
                    $"Path '{ToolPathNormalizer.ForModel(modelPath ?? string.Empty)}' is outside the active workspace. "
                        + "Use a workspace-relative path (e.g. src/foo.cs)."),
            "Invalid path" =>
                ToolResult.Failure(
                    error.Summary,
                    error.Error ?? $"Invalid path '{modelPath}'. Use a workspace-relative path with forward slashes."),
            "Missing argument" when error.Error?.Contains(ToolPathNormalizer.PathArgumentName, StringComparison.Ordinal) == true =>
                ToolResult.Failure(
                    error.Summary,
                    "file_write requires `path` (workspace-relative, forward slashes)."),
            _ => error
        };
    }

    private static string DescribeWriteException(Exception ex, string modelPath)
    {
        var displayPath = ToolPathNormalizer.ForModel(modelPath);
        return ex switch
        {
            UnauthorizedAccessException =>
                $"Access denied writing '{displayPath}'. Check file permissions or whether it is read-only.",
            DirectoryNotFoundException =>
                $"Parent directory for '{displayPath}' does not exist and could not be created.",
            IOException io when io.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase) =>
                $"File '{displayPath}' is locked by another process. Close the handle and retry.",
            PathTooLongException =>
                $"Path too long: '{displayPath}'.",
            IOException io =>
                $"I/O error writing '{displayPath}': {io.Message}",
            _ =>
                $"Failed to write '{displayPath}': {ex.Message}"
        };
    }
}
