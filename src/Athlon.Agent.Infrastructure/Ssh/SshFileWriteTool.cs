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
        "Create or overwrite a file. `content` must be a non-empty string with the full file body.",
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

        if (!invocation.Arguments.TryGetString("content", out var content) || string.IsNullOrEmpty(content))
        {
            return ToolResult.Failure("Invalid content", "content must be a non-empty string.");
        }

        try
        {
            var existing = await client.TryGetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (existing is { IsDirectory: true })
            {
                return ToolResult.Failure("Path is a directory", fullPath);
            }

            await client.WriteTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
            await WorkspaceToolHelper.AuditAsync(
                audit,
                "file_write",
                new { path = fullPath, chars = content.Length, remote = true },
                cancellationToken).ConfigureAwait(false);
            return ToolResult.Success($"Wrote {content.Length} chars to {RemotePathNormalizer.GetFileName(fullPath)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failure("Write failed", ex.Message);
        }
    }
}
