using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Patch;

namespace Athlon.Agent.Infrastructure.Ssh;

public sealed class SshApplyPatchTool(
    WorkspaceGuard guard,
    ISshWorkspaceClient client,
    AuditLogService audit,
    IAppLogger logger,
    IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null,
    IActiveAgentSessionContext? activeSessionContext = null,
    IAgentRunContextAccessor? runContextAccessor = null) : IAgentTool, IRemoteWorkspaceTool
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
        var diagnostics = new PatchDiagnosticsContext(
            _logger,
            runtimeDiagnosticEventSink,
            clock,
            sessionId,
            remote: true,
            patchChars: 0);

        if (!SshWorkspaceToolHelper.TryEnsureConnected(client, out var error))
        {
            diagnostics.Fail("path", error.Error ?? error.Summary);
            return error;
        }

        if (!ToolArguments.TryGetRequired(invocation, "patch", out var patch, out error))
        {
            diagnostics.Fail("path", error.Error ?? error.Summary);
            return error;
        }

        diagnostics.PatchChars = patch.Length;
        if (!UnifiedDiffParser.TryParse(patch, out var files, out var parseError))
        {
            var message = parseError ?? "Could not parse patch.";
            diagnostics.PatchPreview = patch;
            diagnostics.Fail("parse", message);
            return ToolResult.Failure("Invalid patch", message);
        }

        diagnostics.PatchPreview = patch;
        diagnostics.FileCount = files.Count;
        diagnostics.HunkCount = files.Sum(f => f.HunkCount);

        string? pathFilter = null;
        if (invocation.Arguments.TryGetString(ToolPathNormalizer.PathArgumentName, out var rawPath)
            && !string.IsNullOrWhiteSpace(rawPath))
        {
            if (!ToolPathNormalizer.TryNormalizeForFileOperation(rawPath, out pathFilter, out var pathMessage))
            {
                var message = $"{invocation.ToolName}: {pathMessage}";
                diagnostics.PathFilter = rawPath;
                diagnostics.Fail("path", message);
                return ToolResult.Failure("Invalid path", message);
            }
        }

        diagnostics.PathFilter = pathFilter;

        var pending = new List<PendingWrite>();
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
            if (!SshWorkspaceToolHelper.TryResolveNormalizedPath(
                    new ToolInvocation("apply_patch", new Dictionary<string, string> { [ToolPathNormalizer.PathArgumentName] = relativePath }),
                    guard,
                    client,
                    out var fullPath,
                    out error))
            {
                diagnostics.Fail("path", error.Error ?? error.Summary, relativePath);
                return error;
            }

            string? moveFull = null;
            if (file.MoveTo is not null)
            {
                if (!SshWorkspaceToolHelper.TryResolveNormalizedPath(
                        new ToolInvocation("apply_patch", new Dictionary<string, string> { [ToolPathNormalizer.PathArgumentName] = file.MoveTo }),
                        guard,
                        client,
                        out moveFull,
                        out error))
                {
                    diagnostics.Fail("path", error.Error ?? error.Summary, file.MoveTo);
                    return error;
                }

                var existingDest = await client.TryGetFileInfoAsync(moveFull, cancellationToken).ConfigureAwait(false);
                if (existingDest is not null)
                {
                    var message = $"Cannot move onto an existing file: {file.MoveTo}";
                    diagnostics.Fail("exists", message, file.MoveTo);
                    return ToolResult.Failure("File exists", message);
                }
            }

            if (file.Kind == PatchFileKind.Delete)
            {
                var existingDelete = await client.TryGetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (existingDelete is null)
                {
                    alreadyApplied.Add(relativePath);
                    continue;
                }

                var deleted = await client.ReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                pending.Add(new PendingWrite(relativePath, fullPath, deleted, null, PendingKind.Delete));
                continue;
            }

            string? original = null;
            string source;
            if (file.Kind == PatchFileKind.Add)
            {
                var existingNew = await client.TryGetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (existingNew is not null)
                {
                    var message = $"Cannot create file that already exists: {relativePath}";
                    diagnostics.Fail("exists", message, relativePath);
                    return ToolResult.Failure("File exists", message);
                }

                source = string.Empty;
            }
            else
            {
                var existing = await client.TryGetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    var message = $"Cannot patch missing file: {relativePath}";
                    diagnostics.Fail("missing_file", message, relativePath);
                    return ToolResult.Failure("File not found", message);
                }

                original = await client.ReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                source = original;
            }

            var appliedResult = PatchApplier.Apply(source, file);
            if (!appliedResult.Succeeded)
            {
                var message = TextLineEndings.WithNonstandardEolHint($"{relativePath}: {appliedResult.Error}", source);
                diagnostics.Fail("hunk_mismatch", message, relativePath);
                return ToolResult.Failure("Patch failed", message);
            }

            if (file.MoveTo is not null)
            {
                var movedText = appliedResult.Status == PatchApplyStatus.Changed
                    ? TextLineEndings.ToDiskText(source, appliedResult.Text)
                    : source;
                pending.Add(new PendingWrite(file.MoveTo, moveFull!, original ?? source, movedText, PendingKind.Move, SourceFull: fullPath));
                continue;
            }

            if (appliedResult.Status == PatchApplyStatus.AlreadyApplied)
            {
                alreadyApplied.Add(relativePath);
                continue;
            }

            if (appliedResult.Status == PatchApplyStatus.Unchanged)
            {
                unchanged++;
                continue;
            }

            var toWrite = file.Kind == PatchFileKind.Add
                ? appliedResult.Text
                : TextLineEndings.ToDiskText(source, appliedResult.Text);
            pending.Add(new PendingWrite(relativePath, fullPath, original, toWrite));
        }

        if (pending.Count == 0)
        {
            if (alreadyApplied.Count > 0 && unchanged == 0)
            {
                ApplyPatchDiagnostics.ReportSuccess(_logger, clock.ElapsedMs, remote: true, alreadyApplied);
                return ToolResult.Success("Already applied", string.Join(Environment.NewLine, alreadyApplied));
            }

            var noop = matched > 0;
            var message = matched == 0
                ? "Patch contained no matching file hunks."
                : "No-op: patch applied but file content unchanged.";
            diagnostics.Fail("noop", message);
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
                    await client.DeleteFileAsync(change.FullPath, cancellationToken).ConfigureAwait(false);
                    written.Add(change);
                    continue;
                }

                if (change.Kind == PendingKind.Move || (change.Kind == PendingKind.Write && change.Original is null))
                {
                    var directory = Path.GetDirectoryName(change.FullPath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(directory))
                    {
                        await client.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
                    }
                }

                await client.WriteTextAsync(change.FullPath, change.ToWrite ?? string.Empty, cancellationToken).ConfigureAwait(false);
                var readBack = await client.ReadTextAsync(change.FullPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(readBack, change.ToWrite, StringComparison.Ordinal))
                {
                    written.Add(change);
                    await RollbackAsync(client, written, CancellationToken.None).ConfigureAwait(false);
                    var message = $"{change.RelativePath}: written content did not match the patch.";
                    diagnostics.Fail("verify", message, change.RelativePath);
                    return ToolResult.Failure("Patch failed", message);
                }

                if (change.Kind == PendingKind.Move && change.SourceFull is not null)
                {
                    await client.DeleteFileAsync(change.SourceFull, cancellationToken).ConfigureAwait(false);
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

                var message = $"{change.RelativePath}: {ex.Message}";
                diagnostics.Fail("exception", message, change.RelativePath);
                return ToolResult.Failure("Patch failed", message);
            }
        }

        var appliedFiles = written.Select(change => change.RelativePath).ToArray();
        await WorkspaceToolHelper.AuditAsync(
            audit,
            "apply_patch",
            new { files = appliedFiles, remote = true },
            cancellationToken).ConfigureAwait(false);

        ApplyPatchDiagnostics.ReportSuccess(_logger, clock.ElapsedMs, remote: true, appliedFiles);
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
        if (change.Kind == PendingKind.Move)
        {
            await client.DeleteFileAsync(change.FullPath, cancellationToken).ConfigureAwait(false);
            if (change.SourceFull is not null && change.Original is not null)
            {
                var source = await client.TryGetFileInfoAsync(change.SourceFull, cancellationToken).ConfigureAwait(false);
                if (source is null)
                {
                    await client.WriteTextAsync(change.SourceFull, change.Original, cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        if (change.Kind == PendingKind.Delete)
        {
            if (change.Original is not null)
            {
                await client.WriteTextAsync(change.FullPath, change.Original, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (change.Original is null)
        {
            await client.DeleteFileAsync(change.FullPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        await client.WriteTextAsync(change.FullPath, change.Original, cancellationToken).ConfigureAwait(false);
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
