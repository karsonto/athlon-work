using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Infrastructure.Ssh;

public sealed class SshApplyPatchTool(
    WorkspaceGuard guard,
    ISshWorkspaceClient client,
    AuditLogService audit) : IAgentTool, IRemoteWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "apply_patch",
        "Apply a unified diff patch to workspace files. Use when file_edit fails due to exact-match errors. "
            + "Patch must use standard --- / +++ / @@ headers.",
        ToolSchema.Object()
            .String("patch", "Unified diff text (--- / +++ / @@ hunks)", required: true, pattern: @"(?s)^.*(?:--- |\*\*\* Begin Patch).*")
            .String("path", "Workspace-relative path; when set, only hunks for this file are applied")
            .Build(),
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!SshWorkspaceToolHelper.TryEnsureConnected(client, out var error))
        {
            return error;
        }

        if (!ToolArguments.TryGetRequired(invocation, "patch", out var patch, out error))
        {
            return error;
        }

        if (!UnifiedDiffParser.TryParse(patch, out var files, out var parseError))
        {
            return ToolResult.Failure("Invalid patch", parseError ?? "Could not parse patch.");
        }

        string? pathFilter = null;
        if (invocation.Arguments.TryGetString(ToolPathNormalizer.PathArgumentName, out var rawPath)
            && !string.IsNullOrWhiteSpace(rawPath))
        {
            if (!ToolPathNormalizer.TryNormalizeForFileOperation(rawPath, out pathFilter, out var pathMessage))
            {
                return ToolResult.Failure("Invalid path", $"{invocation.ToolName}: {pathMessage}");
            }
        }

        var appliedFiles = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = file.NewPath;
            if (pathFilter is not null
                && !string.Equals(ToolPathNormalizer.ForModel(relativePath), ToolPathNormalizer.ForModel(pathFilter), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!SshWorkspaceToolHelper.TryResolveNormalizedPath(
                    new ToolInvocation("apply_patch", new Dictionary<string, string> { [ToolPathNormalizer.PathArgumentName] = relativePath }),
                    guard,
                    client,
                    out var fullPath,
                    out error))
            {
                return error;
            }

            string content;
            if (file.IsNewFile)
            {
                content = string.Empty;
            }
            else
            {
                var existing = await client.TryGetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    return ToolResult.Failure("File not found", $"Cannot patch missing file: {relativePath}");
                }

                content = await client.ReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }

            var patched = UnifiedDiffApplier.ApplyAllHunks(content, file.Hunks, out var applyError);
            if (applyError is not null)
            {
                return ToolResult.Failure("Patch failed", $"{relativePath}: {applyError}");
            }

            await client.WriteTextAsync(fullPath, patched, cancellationToken).ConfigureAwait(false);
            appliedFiles.Add(relativePath);
        }

        if (appliedFiles.Count == 0)
        {
            return ToolResult.Failure("No files patched", "Patch contained no matching file hunks.");
        }

        await WorkspaceToolHelper.AuditAsync(
            audit,
            "apply_patch",
            new { files = appliedFiles, remote = true },
            cancellationToken).ConfigureAwait(false);
        return ToolResult.Success($"Patched {appliedFiles.Count} file(s)", string.Join(Environment.NewLine, appliedFiles));
    }
}
