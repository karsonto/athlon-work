using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.Patch;

internal enum PatchFileApplyOutcomeKind
{
    Pending,
    AlreadyApplied,
    Unchanged,
    Failed
}

internal readonly record struct PatchFileApplyStepResult(
    PatchFileApplyOutcomeKind Kind,
    PatchPendingWrite? Pending = null,
    ToolResult? Failure = null)
{
    public static PatchFileApplyStepResult WithPending(PatchPendingWrite pending) =>
        new(PatchFileApplyOutcomeKind.Pending, pending);

    public static PatchFileApplyStepResult AlreadyApplied() =>
        new(PatchFileApplyOutcomeKind.AlreadyApplied);

    public static PatchFileApplyStepResult Unchanged() =>
        new(PatchFileApplyOutcomeKind.Unchanged);

    public static PatchFileApplyStepResult Fail(ToolResult failure) =>
        new(PatchFileApplyOutcomeKind.Failed, Failure: failure);
}

internal sealed record PatchFileApplyEnvironment(
    string RelativePath,
    string FullPath,
    string? MoveFullPath,
    PatchDiagnosticsContext Diagnostics);
