namespace Athlon.Agent.Infrastructure.Patch;

internal interface IPatchFileApplier
{
    Task<PatchFileApplyStepResult> ApplyDeleteAsync(
        PatchFile file,
        PatchFileApplyEnvironment environment,
        CancellationToken cancellationToken);

    Task<PatchFileApplyStepResult> ApplyAddAsync(
        PatchFile file,
        PatchFileApplyEnvironment environment,
        CancellationToken cancellationToken);

    Task<PatchFileApplyStepResult> ApplyUpdateAsync(
        PatchFile file,
        PatchFileApplyEnvironment environment,
        CancellationToken cancellationToken);
}
