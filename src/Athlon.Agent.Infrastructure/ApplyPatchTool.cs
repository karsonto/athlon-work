using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Infrastructure;

public sealed class ApplyPatchTool(
    WorkspaceGuard guard,
    AuditLogService audit,
    IAppLogger logger,
    IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null,
    IActiveAgentSessionContext? activeSessionContext = null,
    IAgentRunContextAccessor? runContextAccessor = null) : IAgentTool, ILocalWorkspaceTool
{
    private readonly IAppLogger _logger = logger.ForContext("ApplyPatch");

    public ToolDefinition Definition { get; } = new(
        "apply_patch",
        ToolDescriptions.ApplyPatch,
        ToolSchema.Object()
            .String("patch", "V4A patch (*** Begin Patch) or a unified diff", required: true)
            .String("path", "Workspace-relative path; when set, only hunks for this file are applied")
            .Build(),
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var clock = new ApplyPatchInvokeClock();
        var sessionId = ApplyPatchDiagnostics.ResolveSessionId(activeSessionContext, runContextAccessor);

        if (!ToolArguments.TryGetRequired(invocation, "patch", out var patch, out var error))
        {
            Fail("path", error.Error ?? error.Summary, clock, sessionId, patchChars: 0);
            return error;
        }

        var patchChars = patch.Length;
        if (!UnifiedDiffParser.TryParse(patch, out var files, out var parseError))
        {
            var message = parseError ?? "Could not parse patch.";
            Fail("parse", message, clock, sessionId, patchChars, patchPreview: patch);
            return ToolResult.Failure("Invalid patch", message);
        }

        var fileCount = files.Count;
        var hunkCount = files.Sum(f => f.HunkCount);

        string? pathFilter = null;
        if (invocation.Arguments.TryGetString(ToolPathNormalizer.PathArgumentName, out var rawPath)
            && !string.IsNullOrWhiteSpace(rawPath))
        {
            if (!ToolPathNormalizer.TryNormalizeForFileOperation(rawPath, out pathFilter, out var pathMessage))
            {
                var message = $"{invocation.ToolName}: {pathMessage}";
                Fail("path", message, clock, sessionId, patchChars, pathFilter: rawPath, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                return ToolResult.Failure("Invalid path", message);
            }
        }

        var pending = new List<PendingWrite>();
        var alreadyApplied = new List<string>();
        var matched = 0;
        var unchanged = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = file.Path;
            if (!MatchesFilter(file, pathFilter))
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
                Fail("path", error.Error ?? error.Summary, clock, sessionId, patchChars, path: relativePath, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                return error;
            }

            string? moveFull = null;
            if (file.MoveTo is not null)
            {
                if (!WorkspaceToolHelper.TryResolveNormalizedPath(
                        new ToolInvocation("apply_patch", new Dictionary<string, string> { [ToolPathNormalizer.PathArgumentName] = file.MoveTo }),
                        guard,
                        out moveFull,
                        out error))
                {
                    Fail("path", error.Error ?? error.Summary, clock, sessionId, patchChars, path: file.MoveTo, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                    return error;
                }

                if (File.Exists(moveFull))
                {
                    var message = $"Cannot move onto an existing file: {file.MoveTo}";
                    Fail("exists", message, clock, sessionId, patchChars, path: file.MoveTo, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                    return ToolResult.Failure("File exists", message);
                }
            }

            if (file.Kind == PatchFileKind.Delete)
            {
                if (!File.Exists(fullPath))
                {
                    alreadyApplied.Add(relativePath);
                    continue;
                }

                var deleted = await File.ReadAllTextAsync(fullPath, cancellationToken);
                pending.Add(new PendingWrite(relativePath, fullPath, deleted, null, PendingKind.Delete));
                continue;
            }

            string? original = null;
            string source;
            if (file.Kind == PatchFileKind.Add)
            {
                if (File.Exists(fullPath))
                {
                    var message = $"Cannot create file that already exists: {relativePath}";
                    Fail("exists", message, clock, sessionId, patchChars, path: relativePath, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                    return ToolResult.Failure("File exists", message);
                }

                source = string.Empty;
            }
            else
            {
                if (!File.Exists(fullPath))
                {
                    var message = $"Cannot patch missing file: {relativePath}";
                    Fail("missing_file", message, clock, sessionId, patchChars, path: relativePath, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                    return ToolResult.Failure("File not found", message);
                }

                original = await File.ReadAllTextAsync(fullPath, cancellationToken);
                source = original;
            }

            var applied = PatchApplier.Apply(source, file);
            if (!applied.Succeeded)
            {
                var message = TextLineEndings.WithNonstandardEolHint($"{relativePath}: {applied.Error}", source);
                Fail("hunk_mismatch", message, clock, sessionId, patchChars, path: relativePath, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                return ToolResult.Failure("Patch failed", message);
            }

            if (file.MoveTo is not null)
            {
                var movedText = applied.Status == PatchApplyStatus.Changed
                    ? TextLineEndings.ToDiskText(source, applied.Text)
                    : source;
                pending.Add(new PendingWrite(file.MoveTo, moveFull!, original ?? source, movedText, PendingKind.Move, SourceFull: fullPath));
                continue;
            }

            if (applied.Status == PatchApplyStatus.AlreadyApplied)
            {
                alreadyApplied.Add(relativePath);
                continue;
            }

            if (applied.Status == PatchApplyStatus.Unchanged)
            {
                unchanged++;
                continue;
            }

            var toWrite = file.Kind == PatchFileKind.Add
                ? applied.Text
                : TextLineEndings.ToDiskText(source, applied.Text);
            pending.Add(new PendingWrite(relativePath, fullPath, original, toWrite));
        }

        if (pending.Count == 0)
        {
            if (alreadyApplied.Count > 0 && unchanged == 0)
            {
                ApplyPatchDiagnostics.ReportSuccess(_logger, clock.ElapsedMs, remote: false, alreadyApplied);
                return ToolResult.Success("Already applied", string.Join(Environment.NewLine, alreadyApplied));
            }

            var noop = matched > 0;
            var message = matched == 0
                ? pathFilter is null
                    ? "Patch contained no applicable file hunks."
                    : $"No hunks matched path filter: {pathFilter}"
                : "No-op: patch applied but file content unchanged.";
            Fail("noop", message, clock, sessionId, patchChars, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
            return ToolResult.Failure(noop ? "No-op" : "No files patched", message);
        }

        var written = new List<PendingWrite>();
        foreach (var change in pending)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Kind == PendingKind.Delete)
                {
                    File.Delete(change.FullPath);
                    written.Add(change);
                    continue;
                }

                var directory = Path.GetDirectoryName(change.FullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(change.FullPath, change.ToWrite ?? string.Empty, cancellationToken);
                var readBack = await File.ReadAllTextAsync(change.FullPath, cancellationToken);
                if (!string.Equals(readBack, change.ToWrite, StringComparison.Ordinal))
                {
                    written.Add(change);
                    await RollbackAsync(written, CancellationToken.None);
                    var message = $"{change.RelativePath}: written content did not match the patch.";
                    Fail("verify", message, clock, sessionId, patchChars, path: change.RelativePath, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                    return ToolResult.Failure("Patch failed", message);
                }

                if (change.Kind == PendingKind.Move
                    && change.SourceFull is not null
                    && File.Exists(change.SourceFull))
                {
                    File.Delete(change.SourceFull);
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

                var message = $"{change.RelativePath}: {ex.Message}";
                Fail("exception", message, clock, sessionId, patchChars, path: change.RelativePath, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                return ToolResult.Failure("Patch failed", message);
            }
        }

        var appliedFiles = written.Select(change => change.RelativePath).ToArray();
        await WorkspaceToolHelper.AuditAsync(
            audit,
            "apply_patch",
            new { files = appliedFiles, filtered = pathFilter },
            cancellationToken);

        ApplyPatchDiagnostics.ReportSuccess(_logger, clock.ElapsedMs, remote: false, appliedFiles);

        return ToolResult.Success(
            $"Patched {appliedFiles.Length} file(s)",
            string.Join(Environment.NewLine, appliedFiles));
    }

    private void Fail(
        string stage,
        string error,
        ApplyPatchInvokeClock clock,
        string? sessionId,
        int patchChars,
        string? path = null,
        string? pathFilter = null,
        int fileCount = 0,
        int hunkCount = 0,
        string? patchPreview = null) =>
        ApplyPatchDiagnostics.ReportFailure(
            _logger,
            runtimeDiagnosticEventSink,
            stage,
            error,
            clock.ElapsedMs,
            remote: false,
            patchChars,
            sessionId,
            path,
            pathFilter,
            fileCount,
            hunkCount,
            patchPreview);

    private static async Task RollbackAsync(IReadOnlyList<PendingWrite> written, CancellationToken cancellationToken)
    {
        for (var i = written.Count - 1; i >= 0; i--)
        {
            await RestoreOneAsync(written[i], cancellationToken);
        }
    }

    private static bool MatchesFilter(PatchFile file, string? pathFilter)
    {
        if (pathFilter is null)
        {
            return true;
        }

        var filter = ToolPathNormalizer.ForModel(pathFilter);
        if (string.Equals(ToolPathNormalizer.ForModel(file.Path), filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return file.MoveTo is not null
            && string.Equals(ToolPathNormalizer.ForModel(file.MoveTo), filter, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RestoreOneAsync(PendingWrite change, CancellationToken cancellationToken)
    {
        if (change.Kind == PendingKind.Move)
        {
            if (File.Exists(change.FullPath))
            {
                File.Delete(change.FullPath);
            }

            if (change.SourceFull is not null
                && !File.Exists(change.SourceFull)
                && change.Original is not null)
            {
                await File.WriteAllTextAsync(change.SourceFull, change.Original, cancellationToken);
            }

            return;
        }

        if (change.Kind == PendingKind.Delete)
        {
            if (change.Original is not null)
            {
                await File.WriteAllTextAsync(change.FullPath, change.Original, cancellationToken);
            }

            return;
        }

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

    private enum PendingKind
    {
        Write,
        Delete,
        Move
    }

    private sealed record PendingWrite(
        string RelativePath,
        string FullPath,
        string? Original,
        string? ToWrite,
        PendingKind Kind = PendingKind.Write,
        string? SourceFull = null);
}
