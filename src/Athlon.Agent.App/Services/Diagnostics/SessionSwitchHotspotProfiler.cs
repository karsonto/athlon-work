using System.Diagnostics;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services.Diagnostics;

/// <summary>
/// Per-phase starvation detector for the session switch.
///
/// <para>Problem this solves: a phase can measure ~0ms and still be the reason a switch takes 19
/// seconds. <see cref="SessionSwitchProfiler.Measure"/> brackets the work between two synchronous
/// call points, but when those points sit either side of an <c>await</c> that resumes on the UI
/// thread, the continuation can be queued for seconds before the closing bracket is ever reached.
/// The measured duration then looks fine and the queueing delay is invisible — which is exactly
/// how <c>fullSessionLoad=0ms</c> coexisted with <c>total=13760ms</c>.</para>
///
/// <para>How it detects that: <see cref="Lower"/> is called once the phase's work is known to be
/// done while the calling thread keeps running, and <see cref="Continue"/> is called when the
/// continuation resumes. The gap between them is time the switch spent queued rather than working.
/// Milliseconds of work with seconds of gap means the phase is a victim of UI-thread starvation,
/// not a cost centre.</para>
///
/// <para>Static because the marker points live in different types (view model and WebChatView);
/// threading an instance through both would change public signatures for diagnostics only.
/// Mirrors <see cref="SessionSwitchProfiler"/>.</para>
/// </summary>
internal static class SessionSwitchHotspotProfiler
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, MarkerState> States = new(StringComparer.Ordinal);

    private static bool _enabled = true;

    /// <summary>Enables or disables collection. Safe to call from the startup path.</summary>
    public static void Initialize(bool enabled = true)
    {
        lock (Gate)
        {
            _enabled = enabled;
            States.Clear();
        }
    }

    /// <summary>Drops all markers. Called when a new switch begins.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            States.Clear();
        }
    }

    /// <summary>
    /// Starts a marker for <paramref name="phase"/>. A repeated mark for the same phase replaces
    /// the unfinished one, so a stale gap from a prior switch can never be reported as this one's.
    /// </summary>
    public static void Begin(string phase)
    {
        if (!_enabled)
        {
            return;
        }

        lock (Gate)
        {
            States[phase] = new MarkerState { WorkDoneAt = null };
        }
    }

    /// <summary>
    /// Records that <paramref name="phase"/>'s work finished, while the calling thread continues.
    /// The matching <see cref="Continue"/> reports how long the thread was held up in between.
    /// </summary>
    public static void Lower(string phase)
    {
        if (!_enabled)
        {
            return;
        }

        lock (Gate)
        {
            if (States.TryGetValue(phase, out var state))
            {
                state.WorkDoneAt = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>
    /// Reports that <paramref name="phase"/> resumed after its await, emitting the queueing gap to
    /// the log. Deliberately not routed through <see cref="SessionSwitchProfiler"/>: that sample is
    /// emitted after <c>Complete</c>, and a value written here could land after the flush and be
    /// misattributed to the next switch. Keep this to the log line until the emit lifecycle has a
    /// "late phase" channel.
    /// </summary>
    public static void Continue(string phase, IAppLogger? logger)
    {
        if (!_enabled)
        {
            return;
        }

        double? gapMs = null;
        lock (Gate)
        {
            if (States.TryGetValue(phase, out var state) && state.WorkDoneAt is { } done)
            {
                gapMs = Stopwatch.GetElapsedTime(done).TotalMilliseconds;
                state.WorkDoneAt = null;
            }
        }

        if (gapMs is not { } gap)
        {
            return;
        }

        try
        {
            logger?.Information("session.switch.queue phase={Phase} queued={QueuedMs:0.#}ms", phase, gap);
        }
        catch
        {
            // Instrumentation must never surface a failure into the switch path.
        }
    }

    private sealed class MarkerState
    {
        public long? WorkDoneAt { get; set; }
    }
}
