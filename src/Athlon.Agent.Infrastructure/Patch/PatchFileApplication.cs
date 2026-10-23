namespace Athlon.Agent.Infrastructure.Patch;

/// <summary>Template-method dispatch from <see cref="PatchFile"/> kind to <see cref="IPatchFileApplier"/>.</summary>
internal static class PatchFileApplication
{
    public static Task<PatchFileApplyStepResult> ApplyAsync(
        PatchFile file,
        IPatchFileApplier applier,
        PatchFileApplyEnvironment environment,
        CancellationToken cancellationToken) =>
        file.Kind switch
        {
            PatchFileKind.Delete => applier.ApplyDeleteAsync(file, environment, cancellationToken),
            PatchFileKind.Add => applier.ApplyAddAsync(file, environment, cancellationToken),
            PatchFileKind.Update => applier.ApplyUpdateAsync(file, environment, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(file), file.Kind, "Unknown patch file kind.")
        };
}
