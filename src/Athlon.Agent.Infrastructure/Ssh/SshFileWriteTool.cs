using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Infrastructure.Ssh;

public sealed class SshFileWriteTool(
    WorkspaceGuard guard,
    ISshWorkspaceClient client,
    AuditLogService audit) : IAgentTool, IRemoteWorkspaceTool
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
        if (!SshWorkspaceToolHelper.TryResolveNormalizedPath(invocation, guard, client, out var fullPath, out var error))
        {
            return error;
        }

        if (!FileWriteSupport.TryGetContent(invocation, out var content, out error))
        {
            return error;
        }

        var modelPath = invocation.Arguments.GetString(ToolPathNormalizer.PathArgumentName) ?? fullPath;

        try
        {
            var existing = await client.TryGetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (existing is { IsDirectory: true })
            {
                return ToolResult.Failure(
                    "Path is a directory",
                    $"Cannot write file: '{ToolPathNormalizer.ForModel(modelPath)}' is a directory.");
            }

            string? original = existing is null
                ? null
                : await client.ReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false);

            await client.WriteTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
            var mismatch = await VerifyWrittenAsync(fullPath, content, original, modelPath, cancellationToken).ConfigureAwait(false);
            if (mismatch is not null)
            {
                return mismatch;
            }

            await FileWriteSupport.TryAuditAsync(
                audit,
                "file_write",
                new { path = SshWorkspaceToolHelper.ToAuditPath(guard, fullPath), chars = content.Length, remote = true },
                cancellationToken).ConfigureAwait(false);
            return ToolResult.Success($"Wrote {content.Length} chars to {RemotePathNormalizer.GetFileName(fullPath)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Failure("Write failed", $"Failed to write '{ToolPathNormalizer.ForModel(modelPath)}': {ex.Message}");
        }
    }

    private async Task<ToolResult?> VerifyWrittenAsync(
        string fullPath,
        string content,
        string? original,
        string modelPath,
        CancellationToken cancellationToken)
    {
        string readBack;
        try
        {
            readBack = await client.ReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var restoreError = await TryRestoreAsync(fullPath, original).ConfigureAwait(false);
            return ToolResult.Failure(
                "Write failed",
                AppendRestoreError(
                    $"'{ToolPathNormalizer.ForModel(modelPath)}': could not verify the written content. {ex.Message}",
                    restoreError));
        }

        if (string.Equals(readBack, content, StringComparison.Ordinal))
        {
            return null;
        }

        var mismatchRestoreError = await TryRestoreAsync(fullPath, original).ConfigureAwait(false);
        return ToolResult.Failure(
            "Write failed",
            AppendRestoreError(
                $"'{ToolPathNormalizer.ForModel(modelPath)}': written content did not match the requested content.",
                mismatchRestoreError));
    }

    private async Task<string?> TryRestoreAsync(string fullPath, string? original)
    {
        try
        {
            if (original is null)
            {
                await client.DeleteFileAsync(fullPath, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            await client.WriteTextAsync(fullPath, original, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    private static string AppendRestoreError(string message, string? restoreError) =>
        restoreError is null ? message : message + $" Restoring the previous file failed: {restoreError}";
}
