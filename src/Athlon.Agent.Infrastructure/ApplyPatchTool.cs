using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class ApplyPatchTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool, ILocalWorkspaceTool
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
        if (!ToolArguments.TryGetRequired(invocation, "patch", out var patch, out var error)) return error;

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
            if (!WorkspaceToolHelper.TryResolveNormalizedPath(
                    new ToolInvocation("apply_patch", new Dictionary<string, string> { [ToolPathNormalizer.PathArgumentName] = relativePath }),
                    guard,
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
                if (!File.Exists(fullPath))
                {
                    return ToolResult.Failure("File not found", $"Cannot patch missing file: {relativePath}");
                }

                original = await File.ReadAllTextAsync(fullPath, cancellationToken);
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
                    ? pathFilter is null
                        ? "Patch contained no applicable file hunks."
                        : $"No hunks matched path filter: {pathFilter}"
                    : "Patch did not change any file.");
        }

        var written = new List<PendingWrite>();
        foreach (var change in pending)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(change.FullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(change.FullPath, change.ToWrite, cancellationToken);
                var readBack = await File.ReadAllTextAsync(change.FullPath, cancellationToken);
                if (!string.Equals(readBack, change.ToWrite, StringComparison.Ordinal))
                {
                    written.Add(change);
                    await RollbackAsync(written, CancellationToken.None);
                    return ToolResult.Failure(
                        "Patch failed",
                        $"{change.RelativePath}: written content did not match the patch.");
                }

                written.Add(change);
            }
            catch (Exception ex)
            {
                await RollbackAsync(written, CancellationToken.None);
                await RestoreOneAsync(change, CancellationToken.None);
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
            new { files = appliedFiles, filtered = pathFilter },
            cancellationToken);

        return ToolResult.Success(
            $"Patched {appliedFiles.Length} file(s)",
            string.Join(Environment.NewLine, appliedFiles));
    }

    private static async Task RollbackAsync(IReadOnlyList<PendingWrite> written, CancellationToken cancellationToken)
    {
        for (var i = written.Count - 1; i >= 0; i--)
        {
            await RestoreOneAsync(written[i], cancellationToken);
        }
    }

    private static async Task RestoreOneAsync(PendingWrite change, CancellationToken cancellationToken)
    {
        if (change.Original is null)
        {
            if (File.Exists(change.FullPath))
            {
                File.Delete(change.FullPath);
            }

            return;
        }

        await File.WriteAllTextAsync(change.FullPath, change.Original, cancellationToken);
    }

    private sealed record PendingWrite(string RelativePath, string FullPath, string? Original, string ToWrite);
}
