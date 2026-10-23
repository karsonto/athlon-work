using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;
using ApplyPatchDiagnostics = Athlon.Agent.Infrastructure.ApplyPatchDiagnostics;
using ApplyPatchInvokeClock = Athlon.Agent.Infrastructure.ApplyPatchInvokeClock;

namespace Athlon.Agent.Infrastructure.Patch;

/// <summary>Invoke-scoped apply_patch diagnostic fields for <see cref="ApplyPatchDiagnostics.ReportFailure"/>.</summary>
internal sealed class PatchDiagnosticsContext
{
    public PatchDiagnosticsContext(
        IAppLogger logger,
        IRuntimeDiagnosticEventSink? sink,
        ApplyPatchInvokeClock clock,
        string? sessionId,
        bool remote,
        int patchChars)
    {
        Logger = logger;
        Sink = sink;
        Clock = clock;
        SessionId = sessionId;
        Remote = remote;
        PatchChars = patchChars;
    }

    public IAppLogger Logger { get; }

    public IRuntimeDiagnosticEventSink? Sink { get; }

    public ApplyPatchInvokeClock Clock { get; }

    public string? SessionId { get; }

    public bool Remote { get; }

    public int PatchChars { get; set; }

    public string? PathFilter { get; set; }

    public int FileCount { get; set; }

    public int HunkCount { get; set; }

    public string? PatchPreview { get; set; }

    public void Fail(string stage, string error, string? path = null) =>
        ApplyPatchDiagnostics.ReportFailure(
            Logger,
            Sink,
            stage,
            error,
            Clock.ElapsedMs,
            Remote,
            PatchChars,
            SessionId,
            path,
            PathFilter,
            FileCount,
            HunkCount,
            PatchPreview);
}
