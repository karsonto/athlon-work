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

    /// <summary>Rebuild + publish of the session payload after it has been loaded from disk.</summary>
    public const string Adopt = "adopt";

    /// <summary>
    /// Full payload load + adopt that runs <em>after</em> first paint (the tail of
    /// <c>LoadSessionInternalAsync</c>). Without this phase that tail is invisible while still
    /// being counted in <c>total</c>, which is how a switch could read as 20s with 250ms of phases.
    /// </summary>
    public const string FullSessionLoad = "fullSessionLoad";

    /// <summary>
    /// Time spent resolving session directories by probing the filesystem for nested
    /// sub-agent folders. Kept as a phase (rather than a hidden cost) because that probe runs on
    /// every path resolution and is invisible in every other measurement.
    /// </summary>
    public const string DirectoryProbe = "dirProbe";

    /// <summary>
    /// The post-first-paint tail: after <c>firstPaintDone</c> is signalled, the switch awaits the
    /// adopt task and then refreshes workspace chrome.
    ///
    /// <para>This exists because a switch could report <c>total=19s</c> with every other phase at
    /// ~0ms. An earlier attempt blamed <c>session.json</c> deserialization; the switch's own
    /// measurements proved that wrong (<c>fullSessionLoad=0ms</c>). The unaccounted time lives
    /// here, after the last phase, so this phase makes that span visible instead of inferring it
    /// by subtraction.</para>
    /// </summary>
    public const string TailWait = "tailWait";

    /// <summary>
    /// The skill-catalog rescan path (<c>IAgentSkillCatalog.Reload</c>) invoked by the workspace
    /// refresh. It was the single largest cost on a session switch (~10s) because it walked every
    /// skill folder synchronously on the UI thread; the switch no longer reloads skills, so this
    /// phase is now expected to be sub-millisecond. Kept as a probe: if it regresses, the cache was
    /// invalidated when it should not have been.
    /// </summary>
    public const string SkillReload = "skillReload";

    /// <summary>Workspace tree read/walk for the sidebar.</summary>
    public const string WorkspaceTree = "workspaceTree";
}

/// <summary>Immutable snapshot of one completed session-switch measurement.</summary>
public sealed record SessionSwitchSample(
    string? SessionId,
    SessionSwitchHitKind HitKind,
    IReadOnlyDictionary<string, double> PhaseMs,
    double TotalMs,
    double? EndToEndMs,
    IReadOnlyDictionary<string, double>? PhaseCounts = null,
    /// <summary>
    /// Per phase, the offset from <see cref="SessionSwitchProfiler.Begin"/> at which that phase was
    /// last recorded. This is what makes the unmeasured spans computable: phases are a scatter of
    /// durations with no shared origin, so <c>total</c> minus the latest phase end, and the gaps
    /// between consecutive phase ends, are the only way to see time that no phase covers.
    /// </summary>
    IReadOnlyDictionary<string, double>? PhaseEndMs = null);

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
    private static readonly Dictionary<string, double> PhaseCounts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, double> PhaseEnds = new(StringComparer.Ordinal);
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
            PhaseCounts.Clear();
            PhaseEnds.Clear();
            _sessionId = sessionId;
            _hitKind = hitKind;
            _active = true;
            _emitPending = false;
            _endToEndMs = null;
        }

        Total.Restart();
    }

    /// <summary>
    /// Refines the hit kind as the switch discovers cache hits. Keeps the strongest hit seen:
    /// a switch that reused the payload (replay) and later also restored the DOM snapshot must
    /// still report <see cref="SessionSwitchHitKind.Snapshot"/>, not be downgraded by the earlier,
    /// weaker signal.
    /// </summary>
    public static void SetHitKind(SessionSwitchHitKind hitKind)
    {
        lock (Gate)
        {
            if (_active || _emitPending)
            {
                _hitKind = (SessionSwitchHitKind)Math.Max((int)_hitKind, (int)hitKind);
            }
        }
    }

    /// <summary>
    /// Adds a phase duration in milliseconds. Accepted while the switch is active or inside the
    /// post-<see cref="Complete"/> grace window; ignored otherwise.
    ///
    /// <para>Prefer <see cref="Measure"/> so the duration is measured rather than supplied. A phase
    /// that is only recorded when its scope actually ends (e.g. work started during the switch and
    /// finished after <see cref="Complete"/>) is dropped rather than attributed to whatever switch
    /// happens to be active when the continuation lands.</para>
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
            PhaseEnds[phase] = Total.Elapsed.TotalMilliseconds;

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
    /// Records an invocation count for a phase that is accumulated by repeated
    /// <see cref="Measure"/> scopes. The sum alone is ambiguous: <c>dirProbe=180ms</c> could be one
    /// slow probe or sixty cheap ones, so the count is reported alongside it.
    /// </summary>
    public static void RecordCount(string phase, double count)
    {
        if (string.IsNullOrEmpty(phase) || !double.IsFinite(count) || count < 0)
        {
            return;
        }

        lock (Gate)
        {
            if (!_active && !_emitPending)
            {
                return;
            }

            PhaseCounts[phase] = count;
        }
    }

    /// <summary>
    /// Finalizes the current measurement and schedules the emit. The event is written after a
    /// short grace window so a late <see cref="SessionSwitchPhases.JsRender"/> still lands in the
    /// same sample; a new <see cref="Begin"/> cancels a still-pending emit.
    ///
    /// <para>Must be called exactly once per switch. A phase whose scope ends only after this point
    /// is dropped rather than attributed to whichever switch is active when its continuation lands,
    /// so a background task started during the switch (e.g. the MCP refresh) has to be recorded
    /// before reaching here.</para>
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
            var counts = new Dictionary<string, double>(PhaseCounts, StringComparer.Ordinal);
            var ends = new Dictionary<string, double>(PhaseEnds, StringComparer.Ordinal);
            sample = new SessionSwitchSample(
                _sessionId,
                _hitKind,
                phases,
                Total.Elapsed.TotalMilliseconds,
                _endToEndMs,
                counts,
                ends);
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

        // The single most useful number when total dwarfs every phase: how much of the switch no
        // phase accounted for. Measured from the last phase to complete rather than from the sum of
        // the durations — `firstPaint` is recorded as elapsed-since-Begin, so summing would
        // double-count every phase that ran before it.
        if (sample.PhaseEndMs is { Count: > 0 } ends)
        {
            var lastEnd = ends.Values.Max();
            parts.Add($"lastPhaseEnd={lastEnd:0.#}ms");
            parts.Add($"unmeasured={Math.Max(0, sample.TotalMs - lastEnd):0.#}ms");
        }

        if (sample.EndToEndMs is { } endToEnd)
        {
            parts.Add($"endToEnd={endToEnd:0.#}ms");
        }

        if (sample.PhaseCounts is { Count: > 0 } counts)
        {
            foreach (var (phase, count) in counts.OrderByDescending(p => p.Value))
            {
                parts.Add($"{phase}#={count:0.#}");
            }
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
