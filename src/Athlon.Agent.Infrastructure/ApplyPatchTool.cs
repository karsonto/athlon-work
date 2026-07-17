using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class ApplyPatchTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool, ILocalWorkspaceTool
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

            if (!WorkspaceToolHelper.TryResolveNormalizedPath(
                    new ToolInvocation("apply_patch", new Dictionary<string, string> { [ToolPathNormalizer.PathArgumentName] = relativePath }),
                    guard,
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
                if (!File.Exists(fullPath))
                {
                    return ToolResult.Failure("File not found", $"Cannot patch missing file: {relativePath}");
                }

                content = await File.ReadAllTextAsync(fullPath, cancellationToken);
            }

            var patched = UnifiedDiffApplier.ApplyAllHunks(content, file.Hunks, out var applyError);
            if (applyError is not null)
            {
                return ToolResult.Failure("Patch failed", $"{relativePath}: {applyError}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, patched, cancellationToken);
            appliedFiles.Add(relativePath);
        }

        if (appliedFiles.Count == 0)
        {
            return ToolResult.Failure(
                "No files patched",
                pathFilter is null
                    ? "Patch contained no applicable file hunks."
                    : $"No hunks matched path filter: {pathFilter}");
        }

        await WorkspaceToolHelper.AuditAsync(
            audit,
            "apply_patch",
            new { files = appliedFiles, filtered = pathFilter },
            cancellationToken);

        return ToolResult.Success(
            $"Patched {appliedFiles.Count} file(s)",
            string.Join(Environment.NewLine, appliedFiles));
    }
}
