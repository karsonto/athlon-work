using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure.Patch;

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
        var diagnostics = CreateDiagnosticsContext();

        var parseResult = ParsePatchInput(invocation, diagnostics, out var patch, out var files);
        if (parseResult is not null)
        {
            return parseResult;
        }

        var filterResult = ResolvePathFilter(invocation, diagnostics, out var pathFilter);
        if (filterResult is not null)
        {
            return filterResult;
        }

        var collectResult = await CollectPendingChangesAsync(
            files,
            pathFilter,
            diagnostics,
            cancellationToken);
        if (collectResult.EarlyResult is not null)
        {
            return collectResult.EarlyResult;
        }

        if (collectResult.Pending.Count == 0)
        {
            return HandleNoPendingWrites(
                diagnostics,
                pathFilter,
                collectResult.Matched,
                collectResult.Unchanged,
                collectResult.AlreadyApplied);
        }

        var commitResult = await CommitPendingWritesAsync(
            collectResult.Pending,
            diagnostics,
            cancellationToken);
        if (commitResult.Failure is not null)
        {
            return commitResult.Failure;
        }

        return await AuditAndSucceedAsync(
            commitResult.AppliedFiles!,
            pathFilter,
            diagnostics.Clock,
            cancellationToken);
    }

    private PatchDiagnosticsContext CreateDiagnosticsContext()
    {
        var clock = new ApplyPatchInvokeClock();
        var sessionId = ApplyPatchDiagnostics.ResolveSessionId(activeSessionContext, runContextAccessor);
        return new PatchDiagnosticsContext(
            _logger,
            runtimeDiagnosticEventSink,
            clock,
            sessionId,
            remote: false,
            patchChars: 0);
    }

    private static ToolResult? ParsePatchInput(
        ToolInvocation invocation,
        PatchDiagnosticsContext diagnostics,
        out string patch,
        out IReadOnlyList<PatchFile> files)
    {
        patch = string.Empty;
        files = Array.Empty<PatchFile>();

        if (!ToolArguments.TryGetRequired(invocation, "patch", out patch, out var error))
        {
            diagnostics.Fail("path", error.Error ?? error.Summary);
            return error;
        }

        diagnostics.PatchChars = patch.Length;

        if (!UnifiedDiffParser.TryParse(patch, out files, out var parseError))
        {
            var message = parseError ?? "Could not parse patch.";
            diagnostics.PatchPreview = patch;
            diagnostics.Fail("parse", message);
            return ToolResult.Failure("Invalid patch", message);
        }

        diagnostics.PatchPreview = patch;
        diagnostics.FileCount = files.Count;
        diagnostics.HunkCount = files.Sum(f => f.HunkCount);
        return null;
    }

    private static ToolResult? ResolvePathFilter(
        ToolInvocation invocation,
        PatchDiagnosticsContext diagnostics,
        out string? pathFilter)
    {
        pathFilter = null;
        if (!invocation.Arguments.TryGetString(ToolPathNormalizer.PathArgumentName, out var rawPath)
            || string.IsNullOrWhiteSpace(rawPath))
        {
            diagnostics.PathFilter = null;
            return null;
        }

        if (!ToolPathNormalizer.TryNormalizeForFileOperation(rawPath, out pathFilter, out var pathMessage))
        {
            var message = $"{invocation.ToolName}: {pathMessage}";
            diagnostics.PathFilter = rawPath;
            diagnostics.Fail("path", message);
            return ToolResult.Failure("Invalid path", message);
        }

        diagnostics.PathFilter = pathFilter;
        return null;
    }

    private async Task<CollectPendingResult> CollectPendingChangesAsync(
        IReadOnlyList<PatchFile> files,
        string? pathFilter,
        PatchDiagnosticsContext diagnostics,
        CancellationToken cancellationToken)
    {
        var pending = new List<PatchPendingWrite>();
        var alreadyApplied = new List<string>();
        var matched = 0;
        var unchanged = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = file.Path;
            if (!PatchApplyFilter.Matches(file, pathFilter))
            {
                continue;
            }

            matched++;
            if (!WorkspaceToolHelper.TryResolveNormalizedPath(
                    new ToolInvocation("apply_patch", new Dictionary<string, string> { [ToolPathNormalizer.PathArgumentName] = relativePath }),
                    guard,
                    out var fullPath,
                    out var error))
            {
                diagnostics.Fail("path", error.Error ?? error.Summary, relativePath);
                return CollectPendingResult.Fail(error);
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
                    diagnostics.Fail("path", error.Error ?? error.Summary, file.MoveTo);
                    return CollectPendingResult.Fail(error);
                }

                if (File.Exists(moveFull))
                {
                    var message = $"Cannot move onto an existing file: {file.MoveTo}";
                    diagnostics.Fail("exists", message, file.MoveTo);
                    return CollectPendingResult.Fail(ToolResult.Failure("File exists", message));
                }
            }

            var environment = new PatchFileApplyEnvironment(relativePath, fullPath, moveFull, diagnostics);
            var step = await PatchFileApplication.ApplyAsync(
                file,
                LocalPatchFileApplier.Instance,
                environment,
                cancellationToken);

            switch (step.Kind)
            {
                case PatchFileApplyOutcomeKind.Failed:
                    return CollectPendingResult.Fail(step.Failure!);
                case PatchFileApplyOutcomeKind.Pending:
                    pending.Add(step.Pending!);
                    break;
                case PatchFileApplyOutcomeKind.AlreadyApplied:
                    alreadyApplied.Add(relativePath);
                    break;
                case PatchFileApplyOutcomeKind.Unchanged:
                    unchanged++;
                    break;
            }
        }

        return new CollectPendingResult(pending, alreadyApplied, matched, unchanged, null);
    }

    private ToolResult HandleNoPendingWrites(
        PatchDiagnosticsContext diagnostics,
        string? pathFilter,
        int matched,
        int unchanged,
        IReadOnlyList<string> alreadyApplied)
    {
        if (alreadyApplied.Count > 0 && unchanged == 0)
        {
            ApplyPatchDiagnostics.ReportSuccess(_logger, diagnostics.Clock.ElapsedMs, remote: false, alreadyApplied);
            return ToolResult.Success("Already applied", string.Join(Environment.NewLine, alreadyApplied));
        }

        var noop = matched > 0;
        var message = matched == 0
            ? pathFilter is null
                ? "Patch contained no applicable file hunks."
                : $"No hunks matched path filter: {pathFilter}"
            : "No-op: patch applied but file content unchanged.";
        diagnostics.Fail("noop", message);
        return ToolResult.Failure(noop ? "No-op" : "No files patched", message);
    }

    private async Task<CommitWritesResult> CommitPendingWritesAsync(
        IReadOnlyList<PatchPendingWrite> pending,
        PatchDiagnosticsContext diagnostics,
        CancellationToken cancellationToken)
    {
        var written = new List<PatchPendingWrite>();
        foreach (var change in pending)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Kind == PatchPendingKind.Delete)
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
                    diagnostics.Fail("verify", message, change.RelativePath);
                    return CommitWritesResult.Fail(ToolResult.Failure("Patch failed", message));
                }

                if (change.Kind == PatchPendingKind.Move
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
                diagnostics.Fail("exception", message, change.RelativePath);
                return CommitWritesResult.Fail(ToolResult.Failure("Patch failed", message));
            }
        }

        var appliedFiles = written.Select(change => change.RelativePath).ToArray();
        return CommitWritesResult.Succeed(appliedFiles);
    }

    private async Task<ToolResult> AuditAndSucceedAsync(
        string[] appliedFiles,
        string? pathFilter,
        ApplyPatchInvokeClock clock,
        CancellationToken cancellationToken)
    {
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

    private readonly record struct CollectPendingResult(
        List<PatchPendingWrite> Pending,
        List<string> AlreadyApplied,
        int Matched,
        int Unchanged,
        ToolResult? EarlyResult)
    {
        public static CollectPendingResult Fail(ToolResult failure) =>
            new([], [], 0, 0, failure);
    }

    private readonly record struct CommitWritesResult(ToolResult? Failure, string[]? AppliedFiles)
    {
        public static CommitWritesResult Fail(ToolResult failure) => new(failure, null);

        public static CommitWritesResult Succeed(string[] appliedFiles) => new(null, appliedFiles);
    }

    private static async Task RollbackAsync(IReadOnlyList<PatchPendingWrite> written, CancellationToken cancellationToken)
    {
        for (var i = written.Count - 1; i >= 0; i--)
        {
            await RestoreOneAsync(written[i], cancellationToken);
        }
    }

    private static async Task RestoreOneAsync(PatchPendingWrite change, CancellationToken cancellationToken)
    {
        if (change.Kind == PatchPendingKind.Move)
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

        if (change.Kind == PatchPendingKind.Delete)
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
}
