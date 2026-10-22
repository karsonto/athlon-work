using System.Diagnostics;
using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Infrastructure;

/// <summary>Shared apply_patch failure/success observability for local and SSH tools.</summary>
internal static class ApplyPatchDiagnostics
{
    public const int PatchPreviewChars = 2048;

    public static string TruncatePatch(string? patch) =>
        string.IsNullOrEmpty(patch) || patch.Length <= PatchPreviewChars
            ? patch ?? string.Empty
            : patch[..PatchPreviewChars] + $"\n... [truncated, total {patch.Length} chars]";

    public static void ReportFailure(
        IAppLogger logger,
        IRuntimeDiagnosticEventSink? sink,
        string stage,
        string error,
        long durationMs,
        bool remote,
        int patchChars,
        string? sessionId = null,
        string? path = null,
        string? pathFilter = null,
        int fileCount = 0,
        int hunkCount = 0,
        string? patchPreview = null)
    {
        logger.Warning(
            "apply_patch failed stage={Stage} path={Path} pathFilter={PathFilter} files={FileCount} hunks={HunkCount} patchChars={PatchChars} remote={Remote} durationMs={DurationMs} error={Error}",
            stage,
            path ?? "-",
            pathFilter ?? "-",
            fileCount,
            hunkCount,
            patchChars,
            remote,
            durationMs,
            error);

        if (sink is null)
        {
            return;
        }

        var preview = TruncatePatch(patchPreview);
        var message =
            $"stage={stage}; path={path ?? "-"}; filter={pathFilter ?? "-"}; files={fileCount}; hunks={hunkCount}; " +
            $"patchChars={patchChars}; remote={remote}; durationMs={durationMs}; error={error}";
        if (preview.Length > 0)
        {
            message += $"; patchPreview={preview}";
        }

        var evt = new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: sessionId,
            runId: null,
            turnId: null,
            attemptId: null,
            parentAttemptId: null,
            toolCallId: null,
            messageId: null,
            component: RuntimeDiagnosticComponent.Tool,
            phase: RuntimeDiagnosticPhase.Invoke,
            eventType: "tool.apply_patch_failed",
            severity: RuntimeDiagnosticSeverity.Warning,
            errorCode: RuntimeDiagnosticErrorCodes.ApplyPatchFailed,
            message: message);

        _ = sink.EnqueueAsync(evt, CancellationToken.None);
    }

    public static void ReportSuccess(
        IAppLogger logger,
        long durationMs,
        bool remote,
        IReadOnlyList<string> files)
    {
        logger.Information(
            "apply_patch succeeded files={FileCount} remote={Remote} durationMs={DurationMs} paths={Paths}",
            files.Count,
            remote,
            durationMs,
            string.Join(",", files));
    }

    public static string? ResolveSessionId(
        IActiveAgentSessionContext? activeSession,
        IAgentRunContextAccessor? runContext) =>
        activeSession?.SessionId ?? runContext?.Current?.SessionId;
}

/// <summary>Stopwatch helper so invoke paths can share duration measurement.</summary>
internal sealed class ApplyPatchInvokeClock
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public long ElapsedMs
    {
        get
        {
            _sw.Stop();
            return _sw.ElapsedMilliseconds;
        }
    }
}
