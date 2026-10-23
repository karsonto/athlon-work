using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.Patch;

internal sealed class LocalPatchFileApplier : IPatchFileApplier
{
    public static LocalPatchFileApplier Instance { get; } = new();

    public async Task<PatchFileApplyStepResult> ApplyDeleteAsync(
        PatchFile file,
        PatchFileApplyEnvironment environment,
        CancellationToken cancellationToken)
    {
        var relativePath = environment.RelativePath;
        var fullPath = environment.FullPath;
        if (!File.Exists(fullPath))
        {
            return PatchFileApplyStepResult.AlreadyApplied();
        }

        var deleted = await File.ReadAllTextAsync(fullPath, cancellationToken);
        return PatchFileApplyStepResult.WithPending(
            new PatchPendingWrite(relativePath, fullPath, deleted, null, PatchPendingKind.Delete));
    }

    public async Task<PatchFileApplyStepResult> ApplyAddAsync(
        PatchFile file,
        PatchFileApplyEnvironment environment,
        CancellationToken cancellationToken)
    {
        var relativePath = environment.RelativePath;
        var fullPath = environment.FullPath;
        var diagnostics = environment.Diagnostics;
        if (File.Exists(fullPath))
        {
            var message = $"Cannot create file that already exists: {relativePath}";
            diagnostics.Fail("exists", message, relativePath);
            return PatchFileApplyStepResult.Fail(ToolResult.Failure("File exists", message));
        }

        return await ApplyContentPatchAsync(file, environment, source: string.Empty, original: null, cancellationToken);
    }

    public Task<PatchFileApplyStepResult> ApplyUpdateAsync(
        PatchFile file,
        PatchFileApplyEnvironment environment,
        CancellationToken cancellationToken)
    {
        var relativePath = environment.RelativePath;
        var fullPath = environment.FullPath;
        var diagnostics = environment.Diagnostics;
        if (!File.Exists(fullPath))
        {
            var message = $"Cannot patch missing file: {relativePath}";
            diagnostics.Fail("missing_file", message, relativePath);
            return Task.FromResult(PatchFileApplyStepResult.Fail(ToolResult.Failure("File not found", message)));
        }

        return ApplyContentPatchFromDiskAsync(file, environment, cancellationToken);
    }

    private static async Task<PatchFileApplyStepResult> ApplyContentPatchFromDiskAsync(
        PatchFile file,
        PatchFileApplyEnvironment environment,
        CancellationToken cancellationToken)
    {
        var fullPath = environment.FullPath;
        var original = await File.ReadAllTextAsync(fullPath, cancellationToken);
        return await ApplyContentPatchAsync(file, environment, original, original, cancellationToken);
    }

    private static Task<PatchFileApplyStepResult> ApplyContentPatchAsync(
        PatchFile file,
        PatchFileApplyEnvironment environment,
        string source,
        string? original,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var relativePath = environment.RelativePath;
        var fullPath = environment.FullPath;
        var moveFull = environment.MoveFullPath;
        var diagnostics = environment.Diagnostics;

        var applied = PatchApplier.Apply(source, file);
        if (!applied.Succeeded)
        {
            var message = TextLineEndings.WithNonstandardEolHint($"{relativePath}: {applied.Error}", source);
            diagnostics.Fail("hunk_mismatch", message, relativePath);
            return Task.FromResult(PatchFileApplyStepResult.Fail(ToolResult.Failure("Patch failed", message)));
        }

        if (file.MoveTo is not null)
        {
            var movedText = applied.Status == PatchApplyStatus.Changed
                ? TextLineEndings.ToDiskText(source, applied.Text)
                : source;
            return Task.FromResult(PatchFileApplyStepResult.WithPending(
                new PatchPendingWrite(
                    file.MoveTo,
                    moveFull!,
                    original ?? source,
                    movedText,
                    PatchPendingKind.Move,
                    SourceFull: fullPath)));
        }

        if (applied.Status == PatchApplyStatus.AlreadyApplied)
        {
            return Task.FromResult(PatchFileApplyStepResult.AlreadyApplied());
        }

        if (applied.Status == PatchApplyStatus.Unchanged)
        {
            return Task.FromResult(PatchFileApplyStepResult.Unchanged());
        }

        var toWrite = file.Kind == PatchFileKind.Add
            ? applied.Text
            : TextLineEndings.ToDiskText(source, applied.Text);
        return Task.FromResult(PatchFileApplyStepResult.WithPending(
            new PatchPendingWrite(relativePath, fullPath, original, toWrite)));
    }
}
