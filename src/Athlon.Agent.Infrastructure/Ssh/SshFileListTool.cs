using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Infrastructure.Ssh;

public sealed class SshFileListTool(
    WorkspaceGuard guard,
    ISshWorkspaceClient client,
    AuditLogService audit) : IAgentTool, IRemoteWorkspaceTool, IParallelizableAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "file_list",
        "List files and directories (top-level only, up to 200 entries). "
            + "Directories listed first, then files, both alphabetically. "
            + "Output format: [FILE] relative/path (bytes) or [DIR] relative/path/. "
            + "Respects workspace ignore rules.",
        ToolSchema.Object()
            .String("path", ToolPathDescriptions.OptionalWorkspaceRelativeDirectory)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!SshWorkspaceToolHelper.TryResolveOptionalNormalizedPath(invocation, guard, client, out var fullPath, out var error))
        {
            return error;
        }

        try
        {
            var info = await client.TryGetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                return ToolResult.Failure("Directory not found", fullPath);
            }

            if (!info.IsDirectory)
            {
                return ToolResult.Failure("Not a directory", fullPath);
            }

            var workspaceRoot = guard.Normalize(".");
            var ignorePatterns = guard.GetIgnorePatterns();
            var entries = new List<SshEntry>();
            await foreach (var entry in client.ListAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                if (SshWorkspaceToolHelper.ShouldIgnore(entry.FullPath, ignorePatterns))
                {
                    continue;
                }

                entries.Add(entry);
                if (entries.Count >= 200)
                {
                    break;
                }
            }

            var lines = entries
                .OrderBy(entry => entry.IsDirectory ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(entry =>
                {
                    var relative = SshWorkspaceToolHelper.ToRelative(workspaceRoot, entry.FullPath);
                    return entry.IsDirectory
                        ? $"[DIR]  {relative}/"
                        : $"[FILE] {relative} ({entry.Length} bytes)";
                })
                .ToArray();

            await WorkspaceToolHelper.AuditAsync(
                audit,
                "file_list",
                new { path = SshWorkspaceToolHelper.ToAuditPath(guard, fullPath), count = lines.Length, remote = true },
                cancellationToken).ConfigureAwait(false);

            var listedDir = SshWorkspaceToolHelper.ToRelative(workspaceRoot, fullPath);
            var content = lines.Length == 0
                ? "(empty directory)"
                : string.Join(Environment.NewLine, lines)
                  + Environment.NewLine
                  + "(Paths above are exact remote names. Copy character-for-character.)";
            return ToolResult.Success($"Listed {lines.Length} entries from {listedDir}", content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failure("List failed", ex.Message);
        }
    }
}
