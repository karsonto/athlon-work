using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class FileWriteTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "file_write",
        "Create or overwrite a file. `content` must be a non-empty JSON string with the full file body "
            + "(do not omit, null, or empty). Put `path` before `content` in the arguments object so streaming "
            + "truncation still preserves the path. Do not write large HTML/JS in one shot — write a short skeleton "
            + "then extend with file_edit or apply_patch; keep each content payload small so the JSON closes completely.",
        ToolSchema.Object()
            .String("path", ToolPathDescriptions.WorkspaceRelativePath, required: true, minLength: 1)
            .String(
                "content",
                "Full new file content as a JSON string (non-empty). Do not omit, null, or send a non-string type.",
                required: true,
                minLength: 1)
            .Build(),
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceToolHelper.TryResolveNormalizedPath(invocation, guard, out var fullPath, out var error))
        {
            return EnrichFailure(invocation, error);
        }

        if (!FileWriteSupport.TryGetContent(invocation, out var content, out error))
        {
            return error;
        }

        var modelPath = invocation.Arguments.GetString(ToolPathNormalizer.PathArgumentName) ?? fullPath;

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

            string? original = null;
            if (File.Exists(fullPath))
            {
                original = await File.ReadAllTextAsync(fullPath, cancellationToken);
            }

            Directory.CreateDirectory(parent);
            await AtomicFile.WriteAllTextAsync(fullPath, content, cancellationToken);
            var mismatch = await VerifyWrittenAsync(fullPath, content, original, modelPath, cancellationToken);
            if (mismatch is not null)
            {
                return mismatch;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Failure("Write failed", DescribeWriteException(ex, modelPath));
        }

        await FileWriteSupport.TryAuditAsync(
            audit,
            "file_write",
            new { path = WorkspaceToolHelper.ToAuditPath(guard, fullPath), chars = content.Length },
            cancellationToken);

        var fileName = Path.GetFileName(fullPath);
        return ToolResult.Success($"Wrote {content.Length} chars to {fileName}");
    }

    private static async Task<ToolResult?> VerifyWrittenAsync(
        string fullPath,
        string content,
        string? original,
        string modelPath,
        CancellationToken cancellationToken)
    {
        string readBack;
        try
        {
            readBack = await File.ReadAllTextAsync(fullPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var restoreError = await TryRestoreAsync(fullPath, original);
            return ToolResult.Failure("Write failed", DescribeUnverified(modelPath, ex.Message, restoreError));
        }

        if (string.Equals(readBack, content, StringComparison.Ordinal))
        {
            return null;
        }

        var mismatchRestoreError = await TryRestoreAsync(fullPath, original);
        return ToolResult.Failure("Write failed", DescribeMismatch(modelPath, mismatchRestoreError));
    }

    private static async Task<string?> TryRestoreAsync(string fullPath, string? original)
    {
        try
        {
            if (original is null)
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }

                return null;
            }

            await AtomicFile.WriteAllTextAsync(fullPath, original, CancellationToken.None);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    private static string DescribeMismatch(string modelPath, string? restoreError) =>
        AppendRestoreError($"'{ToolPathNormalizer.ForModel(modelPath)}': written content did not match the requested content.", restoreError);

    private static string DescribeUnverified(string modelPath, string readError, string? restoreError) =>
        AppendRestoreError($"'{ToolPathNormalizer.ForModel(modelPath)}': could not verify the written content. {readError}", restoreError);

    private static string AppendRestoreError(string message, string? restoreError) =>
        restoreError is null ? message : message + $" Restoring the previous file failed: {restoreError}";

    private static ToolResult EnrichFailure(ToolInvocation invocation, ToolResult error)
    {
        if (error.Succeeded)
        {
            return error;
        }

        var modelPath = invocation.Arguments.GetString(ToolPathNormalizer.PathArgumentName);
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
