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
        "Apply a unified diff patch to workspace files. Prefer for multi-line or fragile exact-match edits when file_edit fails. "
            + "Patch must use standard --- / +++ / @@ headers.",
        ToolSchema.Object()
            .String("patch", "Unified diff text (--- / +++ / @@ hunks)", required: true, pattern: @"(?s)^.*--- .*")
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

        var pending = new List<PendingWrite>();
        var matched = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = file.NewPath;
            if (pathFilter is not null
                && !string.Equals(ToolPathNormalizer.ForModel(relativePath), ToolPathNormalizer.ForModel(pathFilter), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matched++;
            if (!SshWorkspaceToolHelper.TryResolveNormalizedPath(
                    new ToolInvocation("apply_patch", new Dictionary<string, string> { [ToolPathNormalizer.PathArgumentName] = relativePath }),
                    guard,
                    client,
                    out var fullPath,
                    out error))
            {
                return error;
            }

            string? original = null;
            string source;
            if (file.IsNewFile)
            {
                source = string.Empty;
            }
            else
            {
                var existing = await client.TryGetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    return ToolResult.Failure("File not found", $"Cannot patch missing file: {relativePath}");
                }

                original = await client.ReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                source = original;
            }

            var patched = UnifiedDiffApplier.ApplyAllHunks(source, file.Hunks, out var applyError);
            if (applyError is not null)
            {
                return ToolResult.Failure("Patch failed", $"{relativePath}: {applyError}");
            }

            var toWrite = file.IsNewFile ? patched : UnifiedDiffApplier.ToDiskText(source, patched);
            if (original is not null && string.Equals(toWrite, original, StringComparison.Ordinal))
            {
                continue;
            }

            pending.Add(new PendingWrite(relativePath, fullPath, original, toWrite));
        }

        if (pending.Count == 0)
        {
            return ToolResult.Failure(
                "No files patched",
                matched == 0
                    ? "Patch contained no matching file hunks."
                    : "Patch did not change any file.");
        }

        var written = new List<PendingWrite>();
        foreach (var change in pending)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await client.WriteTextAsync(change.FullPath, change.ToWrite, cancellationToken).ConfigureAwait(false);
                var readBack = await client.ReadTextAsync(change.FullPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(readBack, change.ToWrite, StringComparison.Ordinal))
                {
                    written.Add(change);
                    await RollbackAsync(client, written, CancellationToken.None).ConfigureAwait(false);
                    return ToolResult.Failure(
                        "Patch failed",
                        $"{change.RelativePath}: written content did not match the patch.");
                }

                written.Add(change);
            }
            catch (Exception ex)
            {
                await RollbackAsync(client, written, CancellationToken.None).ConfigureAwait(false);
                await RestoreOneAsync(client, change, CancellationToken.None).ConfigureAwait(false);
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                return ToolResult.Failure("Patch failed", $"{change.RelativePath}: {ex.Message}");
            }
        }

        var appliedFiles = written.Select(change => change.RelativePath).ToArray();
        await WorkspaceToolHelper.AuditAsync(
            audit,
            "apply_patch",
            new { files = appliedFiles, remote = true },
            cancellationToken).ConfigureAwait(false);
        return ToolResult.Success($"Patched {appliedFiles.Length} file(s)", string.Join(Environment.NewLine, appliedFiles));
    }

    private static async Task RollbackAsync(
        ISshWorkspaceClient client,
        IReadOnlyList<PendingWrite> written,
        CancellationToken cancellationToken)
    {
        for (var i = written.Count - 1; i >= 0; i--)
        {
            await RestoreOneAsync(client, written[i], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RestoreOneAsync(
        ISshWorkspaceClient client,
        PendingWrite change,
        CancellationToken cancellationToken)
    {
        if (change.Original is null)
        {
            await client.DeleteFileAsync(change.FullPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        await client.WriteTextAsync(change.FullPath, change.Original, cancellationToken).ConfigureAwait(false);
    }

    private sealed record PendingWrite(string RelativePath, string FullPath, string? Original, string ToWrite);
}
