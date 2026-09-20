using System.Diagnostics;
using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.App.Services.Diagnostics;

/// <summary>
/// Hit level of a session switch, used to bucket profiler samples so a slow
/// "cold" load is never mixed into the "already visited" latencies.
/// </summary>
public enum SessionSwitchHitKind
{
    /// <summary>Nothing cached: metadata/full load plus a full timeline replay.</summary>
    Cold,

    /// <summary>Session payload reused, but the timeline was replayed from events.</summary>
    Replay,

    /// <summary>Timeline DOM snapshot reused; no event replay was sent to the page.</summary>
    Snapshot
}

/// <summary>Canonical phase names recorded by <see cref="SessionSwitchProfiler"/>.</summary>
public static class SessionSwitchPhases
{
    /// <summary>Flush/checkpoint of the outgoing session before the switch.</summary>
    public const string Prepare = "prepare";

    /// <summary>Session metadata + first display page load.</summary>
    public const string SnapshotLoad = "snapshotLoad";

    /// <summary>Rebuild of the per-session message view models.</summary>
    public const string VmRebuild = "vmRebuild";

    /// <summary>Serialization of the replay event stream (markdown → HTML).</summary>
    public const string ReplayBuild = "replayBuild";

    /// <summary>Posting replay batches into the WebView message channel.</summary>
    public const string PostBatches = "postBatches";

    /// <summary>In-page DOM render time reported back by the timeline script.</summary>
    public const string JsRender = "jsRender";

    /// <summary>Time until the first painted frame of the new session.</summary>
    public const string FirstPaint = "firstPaint";
}

/// <summary>Immutable snapshot of one completed session-switch measurement.</summary>
public sealed record SessionSwitchSample(
    string? SessionId,
    SessionSwitchHitKind HitKind,
    IReadOnlyDictionary<string, double> PhaseMs,
    double TotalMs,
    double? EndToEndMs);

/// <summary>
/// Lightweight, behavior-neutral instrument for session switching. Phases are recorded with
/// <see cref="Record"/> or a <see cref="Measure"/> scope and flushed as one
/// <c>session.switch</c> runtime-diagnostic event (component <c>UiSessionSwitch</c>) plus a
/// log line when <see cref="Complete"/> runs.
///
/// Static by design: the switch pipeline spans MainShellViewModel → SessionTurnUiController →
/// WebChatView, and threading a profiler instance through those constructors would change
/// public signatures for no correctness benefit. This mirrors <c>AppThemeManager</c>.
///
/// Emit is deferred by a short grace window because the chat-view render is fire-and-forget:
/// the shell finishes its part of the switch before the WebView posts <c>replayComplete</c>,
/// so the JS-side <see cref="SessionSwitchPhases.JsRender"/> (and the final paint) would
/// otherwise miss the sample.
/// </summary>
public static class SessionSwitchProfiler
{
    private const string EventType = "session.switch";

    /// <summary>How long <see cref="Complete"/> waits for the late JS render phase.</summary>
    internal static readonly TimeSpan EmitGrace = TimeSpan.FromMilliseconds(500);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, double> PhaseMs = new(StringComparer.Ordinal);
    private static readonly Stopwatch Total = new();

    private static IAppLogger? _logger;
    private static IRuntimeDiagnosticEventSink? _sink;
    private static bool _enabled = true;

    private static string? _sessionId;
    private static SessionSwitchHitKind _hitKind = SessionSwitchHitKind.Cold;
    private static bool _active;
    private static bool _emitPending;
    private static double? _endToEndMs;
    private static SessionSwitchSample? _lastSample;

    /// <summary>Last completed sample (primarily for tests and diagnostics).</summary>
    public static SessionSwitchSample? LastSample
    {
        get
        {
            lock (Gate)
            {
                return _lastSample;
            }
        }
    }

    /// <summary>True while a switch is being measured (including the pending-emit grace).</summary>
    public static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                return _active || _emitPending;
            }
        }
    }

    /// <summary>Elapsed time since <see cref="Begin"/>, in milliseconds.</summary>
    public static double ElapsedMilliseconds => Total.Elapsed.TotalMilliseconds;

    /// <summary>Wire the log sink and runtime-diagnostic sink. Safe to call once at startup.</summary>
    public static void Initialize(
        IAppLogger? logger,
        IRuntimeDiagnosticEventSink? sink = null,
        bool enabled = true)
    {
        lock (Gate)
        {
            _logger = logger;
            _sink = sink;
            _enabled = enabled;
        }
    }

    /// <summary>Starts a new measurement, discarding any in-flight or pending one.</summary>
    public static void Begin(string? sessionId, SessionSwitchHitKind hitKind = SessionSwitchHitKind.Cold)
    {
        lock (Gate)
        {
            PhaseMs.Clear();
            _sessionId = sessionId;
            _hitKind = hitKind;
            _active = true;
            _emitPending = false;
            _endToEndMs = null;
        }

        Total.Restart();
    }

    /// <summary>Refines the hit kind as the switch discovers cache hits.</summary>
    public static void SetHitKind(SessionSwitchHitKind hitKind)
    {
        lock (Gate)
        {
            if (_active || _emitPending)
            {
                _hitKind = hitKind;
            }
        }
    }

    /// <summary>
    /// Adds a phase duration in milliseconds. Accepted while the switch is active or inside the
    /// post-<see cref="Complete"/> grace window; ignored otherwise.
    /// </summary>
    public static void Record(string phase, double milliseconds)
    {
        if (string.IsNullOrEmpty(phase) || !double.IsFinite(milliseconds) || milliseconds < 0)
        {
            return;
        }

        var emitNow = false;
        lock (Gate)
        {
            if (!_active && !_emitPending)
            {
                return;
            }

            PhaseMs[phase] = PhaseMs.GetValueOrDefault(phase) + milliseconds;

            // A late jsRender is the last signal we expect; flush as soon as it arrives.
            if (_emitPending && phase == SessionSwitchPhases.JsRender)
            {
                emitNow = true;
            }
        }

        if (emitNow)
        {
            TryEmit();
        }
    }

    /// <summary>Adds a phase duration. Accepted while active or inside the emit grace window.</summary>
    public static void Record(string phase, TimeSpan elapsed) => Record(phase, elapsed.TotalMilliseconds);

    public static void RecordSince(string phase, long startTimestamp) =>
        Record(phase, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

    /// <summary>Records the elapsed-since-<see cref="Begin"/> value as a phase (e.g. first paint).</summary>
    public static void RecordElapsed(string phase) => Record(phase, Total.Elapsed.TotalMilliseconds);

    /// <summary>Measures the phase for the lifetime of the returned scope.</summary>
    public static IDisposable Measure(string phase) => new MeasureScope(phase);

    /// <summary>
    /// Finalizes the current measurement and schedules the emit. The event is written after a
    /// short grace window so a late <see cref="SessionSwitchPhases.JsRender"/> still lands in the
    /// same sample; a new <see cref="Begin"/> cancels a still-pending emit.
    /// </summary>
    public static void Complete(double? endToEndMs = null)
    {
        lock (Gate)
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            _emitPending = true;
            _endToEndMs = endToEndMs;
        }

        Total.Stop();

        if (HasPhaseLocked(SessionSwitchPhases.JsRender))
        {
            TryEmit();
            return;
        }

        ScheduleGraceEmit();
    }

    private static bool HasPhaseLocked(string phase)
    {
        lock (Gate)
        {
            return PhaseMs.ContainsKey(phase);
        }
    }

    private static void ScheduleGraceEmit()
    {
        _ = Task.Delay(EmitGrace)
            .ContinueWith(
                static _ => TryEmit(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private static void TryEmit()
    {
        SessionSwitchSample? sample = null;
        lock (Gate)
        {
            if (!_emitPending)
            {
                return;
            }

            _emitPending = false;
            var phases = new Dictionary<string, double>(PhaseMs, StringComparer.Ordinal);
            sample = new SessionSwitchSample(
                _sessionId,
                _hitKind,
                phases,
                Total.Elapsed.TotalMilliseconds,
                _endToEndMs);
            _lastSample = sample;
        }

        Emit(sample);
    }

    private static void Emit(SessionSwitchSample sample)
    {
        if (!_enabled)
        {
            return;
        }

        var message = FormatSample(sample);

        try
        {
            _logger?.Information("{EventType} {Switch}", EventType, message);
        }
        catch
        {
            // Instrumentation must never surface a failure into the switch path.
        }

        var sink = _sink;
        if (sink is null)
        {
            return;
        }

        var evt = new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: sample.SessionId,
            runId: null,
            turnId: null,
            attemptId: null,
            parentAttemptId: null,
            toolCallId: null,
            messageId: null,
            component: RuntimeDiagnosticComponent.UiSessionSwitch,
            phase: RuntimeDiagnosticPhase.Switch,
            eventType: EventType,
            severity: RuntimeDiagnosticSeverity.Debug,
            message: message);

        _ = sink.EnqueueAsync(evt, CancellationToken.None);
    }

    /// <summary>Stable, greppable one-line rendering: <c>hit=replay total=123.4ms prepare=1.2 ...</c></summary>
    internal static string FormatSample(SessionSwitchSample sample)
    {
        var parts = new List<string>(sample.PhaseMs.Count + 2)
        {
            $"hit={HitKindName(sample.HitKind)}",
            $"total={sample.TotalMs:0.#}ms"
        };

        foreach (var (phase, ms) in sample.PhaseMs.OrderByDescending(p => p.Value))
        {
            parts.Add($"{phase}={ms:0.#}ms");
        }

        if (sample.EndToEndMs is { } endToEnd)
        {
            parts.Add($"endToEnd={endToEnd:0.#}ms");
        }

        return string.Join(' ', parts);
    }

    private static string HitKindName(SessionSwitchHitKind kind) => kind switch
    {
        SessionSwitchHitKind.Snapshot => "snapshot",
        SessionSwitchHitKind.Replay => "replay",
        _ => "cold"
    };

    private sealed class MeasureScope(string phase) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            RecordSince(phase, _start);
        }
    }
}
