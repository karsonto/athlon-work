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
            .String("patch", "Unified diff text (--- / +++ / @@ hunks)", required: true, pattern: @"(?s)^.*--- .*")
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
        var hunkCount = files.Sum(f => f.Hunks.Count);

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
                Fail("path", error.Error ?? error.Summary, clock, sessionId, patchChars, path: relativePath, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                return error;
            }

            string? original = null;
            string source;
            if (file.IsNewFile)
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

            var patched = UnifiedDiffApplier.ApplyAllHunks(source, file.Hunks, out var applyError);
            if (applyError is not null)
            {
                var message = TextLineEndings.WithNonstandardEolHint($"{relativePath}: {applyError}", source);
                Fail("hunk_mismatch", message, clock, sessionId, patchChars, path: relativePath, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                return ToolResult.Failure("Patch failed", message);
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
                    var message = $"{change.RelativePath}: written content did not match the patch.";
                    Fail("verify", message, clock, sessionId, patchChars, path: change.RelativePath, pathFilter: pathFilter, fileCount: fileCount, hunkCount: hunkCount, patchPreview: patch);
                    return ToolResult.Failure("Patch failed", message);
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
