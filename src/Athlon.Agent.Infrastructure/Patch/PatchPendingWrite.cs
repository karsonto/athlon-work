namespace Athlon.Agent.Infrastructure.Patch;

internal enum PatchPendingKind
{
    Write,
    Delete,
    Move
}

internal sealed record PatchPendingWrite(
    string RelativePath,
    string FullPath,
    string? Original,
    string? ToWrite,
    PatchPendingKind Kind = PatchPendingKind.Write,
    string? SourceFull = null);
