using System.Collections.Concurrent;
using Athlon.Agent.App.Services.Diagnostics;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Tests;

/// <summary>
/// Serializes tests that share process-wide static state. The switch profiler's phases are static,
/// and <see cref="SessionDirectoryLayoutTests"/> resets the directory-probe counters on that same
/// timer, so both collections must be disabled from parallelization together.
/// </summary>
public static class SessionSwitchProfilerCollection
{
    public const string Name = "session-switch-profiler";
}

[CollectionDefinition(SessionSwitchProfilerCollection.Name, DisableParallelization = true)]
public sealed class SessionSwitchProfilerCollectionDefinition;

[Collection(SessionSwitchProfilerCollection.Name)]
public sealed class SessionSwitchProfilerTests
{
    [Fact]
    public void Complete_aggregates_phases_and_reports_sample()
    {
        SessionSwitchProfiler.Initialize(logger: null, sink: null, enabled: true);
        SessionSwitchProfiler.Begin("s1", SessionSwitchHitKind.Replay);
        SessionSwitchProfiler.Record(SessionSwitchPhases.Prepare, 3.0);
        SessionSwitchProfiler.Record(SessionSwitchPhases.Prepare, 2.0);
        SessionSwitchProfiler.Record(SessionSwitchPhases.ReplayBuild, 10.0);
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 40.0);
        SessionSwitchProfiler.Complete(endToEndMs: 100.0);

        var sample = SessionSwitchProfiler.LastSample;
        Assert.NotNull(sample);
        Assert.Equal("s1", sample!.SessionId);
        Assert.Equal(SessionSwitchHitKind.Replay, sample.HitKind);
        Assert.Equal(5.0, sample.PhaseMs[SessionSwitchPhases.Prepare], 3);
        Assert.Equal(10.0, sample.PhaseMs[SessionSwitchPhases.ReplayBuild], 3);
        Assert.Equal(40.0, sample.PhaseMs[SessionSwitchPhases.JsRender], 3);
        Assert.Equal(100.0, sample.EndToEndMs);
    }

    [Fact]
    public void Record_is_ignored_when_no_switch_is_active()
    {
        SessionSwitchProfiler.Initialize(logger: null, sink: null, enabled: true);
        SessionSwitchProfiler.Begin("idle", SessionSwitchHitKind.Cold);
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 1.0);
        SessionSwitchProfiler.Complete();

        // Everything below happens after the switch (and its grace window) is done.
        SessionSwitchProfiler.Record(SessionSwitchPhases.ReplayBuild, 999.0);

        var sample = SessionSwitchProfiler.LastSample;
        Assert.NotNull(sample);
        Assert.False(sample!.PhaseMs.ContainsKey(SessionSwitchPhases.ReplayBuild));
    }

    [Fact]
    public void Emits_structured_event_with_hit_kind_and_phases()
    {
        var sink = new CapturingSink();
        SessionSwitchProfiler.Initialize(logger: null, sink: sink, enabled: true);
        SessionSwitchProfiler.Begin("s2", SessionSwitchHitKind.Cold);
        SessionSwitchProfiler.SetHitKind(SessionSwitchHitKind.Snapshot);
        SessionSwitchProfiler.Record(SessionSwitchPhases.VmRebuild, 7.0);
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 12.0);
        SessionSwitchProfiler.Complete();

        var evt = Assert.Single(sink.Events);
        Assert.Equal("session.switch", evt.eventType);
        Assert.Equal(RuntimeDiagnosticComponent.UiSessionSwitch, evt.component);
        Assert.Equal(RuntimeDiagnosticPhase.Switch, evt.phase);
        Assert.Equal("s2", evt.sessionId);
        Assert.Contains("hit=snapshot", evt.message);
        Assert.Contains("vmRebuild=7", evt.message);
    }

    [Fact]
    public async Task Late_js_render_lands_in_sample_within_grace()
    {
        var sink = new CapturingSink();
        SessionSwitchProfiler.Initialize(logger: null, sink: sink, enabled: true);
        SessionSwitchProfiler.Begin("s3", SessionSwitchHitKind.Replay);
        SessionSwitchProfiler.Record(SessionSwitchPhases.FirstPaint, 5.0);

        // The shell completes before the page reports its render time; the emit is deferred so the
        // late jsRender still belongs to this sample.
        SessionSwitchProfiler.Complete();
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 33.0);

        var evt = await WaitForEventAsync(sink).ConfigureAwait(false);
        Assert.Contains("hit=replay", evt.message);
        Assert.Contains("jsRender=33", evt.message);
    }

    [Fact]
    public void New_begin_discards_a_pending_emit()
    {
        var sink = new CapturingSink();
        SessionSwitchProfiler.Initialize(logger: null, sink: sink, enabled: true);

        SessionSwitchProfiler.Begin("old", SessionSwitchHitKind.Cold);
        SessionSwitchProfiler.Complete();

        SessionSwitchProfiler.Begin("new", SessionSwitchHitKind.Replay);
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 2.0);
        SessionSwitchProfiler.Complete();

        var sample = SessionSwitchProfiler.LastSample;
        Assert.NotNull(sample);
        Assert.Equal("new", sample!.SessionId);
        Assert.Single(sink.Events);
        Assert.True(sink.Events.TryPeek(out var emitted));
        Assert.Equal("new", emitted.sessionId);
    }

    [Fact]
    public void Disabled_profiler_records_sample_without_emitting()
    {
        var sink = new CapturingSink();
        SessionSwitchProfiler.Initialize(logger: null, sink: sink, enabled: false);
        SessionSwitchProfiler.Begin("s4", SessionSwitchHitKind.Cold);
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 1.0);
        SessionSwitchProfiler.Complete();

        Assert.NotNull(SessionSwitchProfiler.LastSample);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void FormatSample_orders_phases_by_descending_duration()
    {
        var sample = new SessionSwitchSample(
            "fmt",
            SessionSwitchHitKind.Replay,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [SessionSwitchPhases.Prepare] = 1.0,
                [SessionSwitchPhases.JsRender] = 50.0
            },
            TotalMs: 60.0,
            EndToEndMs: null);

        var text = SessionSwitchProfiler.FormatSample(sample);

        Assert.StartsWith("hit=replay total=60", text);
        Assert.True(
            text.IndexOf(SessionSwitchPhases.JsRender, StringComparison.Ordinal)
                < text.IndexOf(SessionSwitchPhases.Prepare, StringComparison.Ordinal));
        Assert.DoesNotContain("endToEnd", text);
    }

    [Fact]
    public void SetHitKind_never_downgrades_a_stronger_hit()
    {
        SessionSwitchProfiler.Initialize(logger: null, sink: null, enabled: true);

        // The payload was reused (replay) and then the DOM snapshot was restored on top of it.
        SessionSwitchProfiler.Begin("s5");
        SessionSwitchProfiler.SetHitKind(SessionSwitchHitKind.Replay);
        SessionSwitchProfiler.SetHitKind(SessionSwitchHitKind.Snapshot);
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 1.0);
        SessionSwitchProfiler.Complete();

        Assert.Equal(SessionSwitchHitKind.Snapshot, SessionSwitchProfiler.LastSample!.HitKind);

        // The reverse order must also stay at snapshot: a later weaker signal cannot undo it.
        SessionSwitchProfiler.Begin("s6");
        SessionSwitchProfiler.SetHitKind(SessionSwitchHitKind.Snapshot);
        SessionSwitchProfiler.SetHitKind(SessionSwitchHitKind.Replay);
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 1.0);
        SessionSwitchProfiler.Complete();

        Assert.Equal(SessionSwitchHitKind.Snapshot, SessionSwitchProfiler.LastSample!.HitKind);
    }

    [Fact]
    public void Records_phase_counts_and_renders_probe_count()
    {
        SessionSwitchProfiler.Initialize(logger: null, sink: null, enabled: true);
        SessionSwitchProfiler.Begin("counted");
        SessionSwitchProfiler.Record(
            SessionSwitchPhases.DirectoryProbe,
            SessionDirectoryLayout.ProbeElapsed.TotalMilliseconds);
        SessionSwitchProfiler.RecordCount("dirProbe", 3);
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 2.0);
        SessionSwitchProfiler.Complete(endToEndMs: 42.0);

        var sample = SessionSwitchProfiler.LastSample!;
        Assert.Equal(3, sample.PhaseCounts!["dirProbe"]);

        var text = SessionSwitchProfiler.FormatSample(sample);
        Assert.Contains("dirProbe#=3", text);
        Assert.Contains("endToEnd=42", text);
    }

    [Fact]
    public void FormatSample_reports_the_unmeasured_span()
    {
        // The failure mode this guards: phases summing to milliseconds while total is seconds.
        // Without this number the gap is invisible and every phase looks innocent — which is how
        // a 19s switch was reported with fullSessionLoad=0ms and adopt=0ms.
        SessionSwitchProfiler.Initialize(logger: null, sink: null, enabled: true);
        SessionSwitchProfiler.Begin("gapped");
        SessionSwitchProfiler.Record(SessionSwitchPhases.FullSessionLoad, 1.0);
        SessionSwitchProfiler.Record(SessionSwitchPhases.Adopt, 1.0);
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 1.0);
        SessionSwitchProfiler.Complete(endToEndMs: 19062.0);

        var sample = SessionSwitchProfiler.LastSample!;

        // Every phase recorded an end offset, so the span after the last one is attributable.
        Assert.Equal(sample.PhaseMs.Count, sample.PhaseEndMs!.Count);

        var text = SessionSwitchProfiler.FormatSample(sample);
        Assert.Contains("lastPhaseEnd=", text);
        Assert.Contains("unmeasured=", text);
    }

    [Fact]
    public void Phase_end_offsets_track_the_latest_record_for_each_phase()
    {
        SessionSwitchProfiler.Initialize(logger: null, sink: null, enabled: true);
        SessionSwitchProfiler.Begin("offsets");
        SessionSwitchProfiler.Record(SessionSwitchPhases.Prepare, 5.0);
        SessionSwitchProfiler.Record(SessionSwitchPhases.Prepare, 5.0);
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 1.0);
        SessionSwitchProfiler.Complete();

        var sample = SessionSwitchProfiler.LastSample!;

        // The duration accumulates across records; the end offset is the latest of them.
        Assert.Equal(10.0, sample.PhaseMs[SessionSwitchPhases.Prepare], 3);
        Assert.True(
            sample.PhaseEndMs![SessionSwitchPhases.Prepare]
                <= sample.PhaseEndMs[SessionSwitchPhases.JsRender]);
    }

    [Fact]
    public void Begin_clears_phase_end_offsets_from_the_previous_switch()
    {
        SessionSwitchProfiler.Initialize(logger: null, sink: null, enabled: true);
        SessionSwitchProfiler.Begin("first");
        SessionSwitchProfiler.Record(SessionSwitchPhases.Prepare, 1.0);
        SessionSwitchProfiler.Complete();

        SessionSwitchProfiler.Begin("second");
        SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, 1.0);
        SessionSwitchProfiler.Complete();

        // A stale offset would make the next sample's `unmeasured` meaningless.
        var sample = SessionSwitchProfiler.LastSample!;
        Assert.False(sample.PhaseEndMs!.ContainsKey(SessionSwitchPhases.Prepare));
        Assert.True(sample.PhaseEndMs.ContainsKey(SessionSwitchPhases.JsRender));
    }

    private static async Task<RuntimeDiagnosticEvent> WaitForEventAsync(CapturingSink sink)    {
        for (var i = 0; i < 100 && sink.Events.IsEmpty; i++)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.False(sink.Events.IsEmpty, "Expected a session.switch event within the grace window.");
        Assert.True(sink.Events.TryPeek(out var evt));
        return evt;
    }

    private sealed class CapturingSink : IRuntimeDiagnosticEventSink
    {
        public ConcurrentQueue<RuntimeDiagnosticEvent> Events { get; } = new();

        public ValueTask EnqueueAsync(RuntimeDiagnosticEvent evt, CancellationToken cancellationToken = default)
        {
            Events.Enqueue(evt);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
