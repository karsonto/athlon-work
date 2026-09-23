using System.Diagnostics;

namespace Athlon.Agent.App.Services.ComputerUse;

/// <summary>
/// Per-action latency breakdown, written to the audit log so the observe/interact loop can be
/// profiled from real usage instead of guesswork.
/// </summary>
internal sealed class ComputerUsePhaseTimings
{
    public double OverlayHideMs { get; set; }

    public double UiaResolveMs { get; set; }

    public double InputMs { get; set; }

    public double SettleMs { get; set; }

    public double CaptureMs { get; set; }

    public double TotalMs { get; set; }

    // ---- Observation shape (token baseline) -------------------------------

    public int UiTreeNodes { get; set; }

    public int UiTreeChars { get; set; }

    public int MaxTreeDepth { get; set; }

    public int MaxNodes { get; set; }

    public object ToPayload() => new
    {
        overlay_hide_ms = Math.Round(OverlayHideMs, 1),
        uia_resolve_ms = Math.Round(UiaResolveMs, 1),
        input_ms = Math.Round(InputMs, 1),
        settle_ms = Math.Round(SettleMs, 1),
        capture_ms = Math.Round(CaptureMs, 1),
        total_ms = Math.Round(TotalMs, 1)
    };
}

/// <summary>Helpers for the millisecond timings recorded by the automation host.</summary>
internal static class ComputerUseTiming
{
    public static long Stamp() => Stopwatch.GetTimestamp();

    public static double ElapsedMs(long sinceStamp) =>
        Stopwatch.GetElapsedTime(sinceStamp).TotalMilliseconds;

    /// <summary>
    /// Runs <paramref name="action"/> and reports its duration in milliseconds. Keeps the
    /// telemetry out of the hot path so the control flow stays readable.
    /// </summary>
    public static async Task<(T Result, double ElapsedMs)> TimedAsync<T>(Func<Task<T>> action)
    {
        var started = Stamp();
        var result = await action().ConfigureAwait(false);
        return (result, ElapsedMs(started));
    }
}
