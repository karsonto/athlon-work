using System.Text;
using Athlon.Agent.App.Services.Diagnostics;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

/// <summary>
/// Covers <see cref="SessionSwitchHotspotProfiler"/>, the starvation detector that explains how a
/// phase can measure ~0ms while the switch takes tens of seconds: the phase's work is trivial, but
/// its continuation waits for the UI thread.
/// </summary>
[Collection(SessionSwitchProfilerCollection.Name)]
public sealed class SessionSwitchHotspotProfilerTests
{
    [Fact]
    public void Initialize_clears_state_from_a_previous_switch()
    {
        SessionSwitchHotspotProfiler.Initialize();
        SessionSwitchHotspotProfiler.Begin("phase");
        SessionSwitchHotspotProfiler.Lower("phase");

        SessionSwitchHotspotProfiler.Initialize();

        // Re-initializing must not carry a pending marker into the next switch; a stale marker would
        // report the gap between two unrelated switches as one phase's queueing delay.
        SessionSwitchHotspotProfiler.Continue("phase", logger: null);
    }

    [Fact]
    public void Reset_clears_pending_markers()
    {
        SessionSwitchHotspotProfiler.Initialize();
        SessionSwitchHotspotProfiler.Begin("phase");
        SessionSwitchHotspotProfiler.Reset();

        // After Reset the phase is unknown, so Continue is a no-op rather than a stale reading.
        SessionSwitchHotspotProfiler.Continue("phase", logger: null);
    }

    [Fact]
    public void Begin_replaces_an_unfinished_marker_for_the_same_phase()
    {
        SessionSwitchHotspotProfiler.Initialize();

        // A switch that starts before the previous one settled must not attribute the earlier
        // lower() to the new switch.
        SessionSwitchHotspotProfiler.Begin("phase");
        SessionSwitchHotspotProfiler.Lower("phase");
        SessionSwitchHotspotProfiler.Begin("phase");

        // The stale lower() was dropped by the second Begin, so there is no gap to report yet.
        var logger = new RecordingLogger();
        SessionSwitchHotspotProfiler.Continue("phase", logger);
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public void Unbalanced_markers_never_throw()
    {
        SessionSwitchHotspotProfiler.Initialize();

        // Instrumentation sits on the critical path of every session switch; it must degrade to a
        // no-op rather than surface an exception into the switch.
        SessionSwitchHotspotProfiler.Lower("never-begun");
        SessionSwitchHotspotProfiler.Continue("never-begun", logger: null);

        SessionSwitchHotspotProfiler.Begin("begun");
        SessionSwitchHotspotProfiler.Continue("begun", logger: null);

        SessionSwitchHotspotProfiler.Begin("lowered");
        SessionSwitchHotspotProfiler.Lower("lowered");
        SessionSwitchHotspotProfiler.Lower("lowered");
        SessionSwitchHotspotProfiler.Continue("lowered", logger: null);
        SessionSwitchHotspotProfiler.Continue("lowered", logger: null);
    }

    [Fact]
    public async Task Continue_reports_the_gap_once_a_marker_is_lowered()
    {
        SessionSwitchHotspotProfiler.Initialize();
        SessionSwitchHotspotProfiler.Begin("adoptAwait");
        SessionSwitchHotspotProfiler.Lower("adoptAwait");

        // Stand in for the UI thread being busy: this is how long the continuation could not run.
        await Task.Delay(50).ConfigureAwait(false);

        var logger = new RecordingLogger();
        SessionSwitchHotspotProfiler.Continue("adoptAwait", logger);

        // Logged rather than emitted: the profiler's sample is flushed after Complete, so a value
        // written this late could be misattributed to the next switch. See the type's remarks.
        var line = Assert.Single(logger.Lines);
        Assert.Contains("session.switch.queue", line);
        Assert.Contains("phase=adoptAwait", line);
        Assert.Contains("queued=", line);
    }

    [Fact]
    public void Continue_records_each_gap_at_most_once()
    {
        SessionSwitchHotspotProfiler.Initialize();
        SessionSwitchHotspotProfiler.Begin("adoptAwait");
        SessionSwitchHotspotProfiler.Lower("adoptAwait");

        var logger = new RecordingLogger();
        SessionSwitchHotspotProfiler.Continue("adoptAwait", logger);

        // The second Continue has no fresh lower() behind it, so it must not double-report.
        SessionSwitchHotspotProfiler.Continue("adoptAwait", logger);

        Assert.Single(logger.Lines);
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Lines { get; } = new();

        public void Debug(string messageTemplate, params object[] values) =>
            Lines.Add(Render(messageTemplate, values));

        public void Information(string messageTemplate, params object[] values) =>
            Lines.Add(Render(messageTemplate, values));

        public void Warning(string messageTemplate, params object[] values) =>
            Lines.Add(Render(messageTemplate, values));

        public void Error(Exception exception, string messageTemplate, params object[] values) =>
            Lines.Add(Render(messageTemplate, values));

        public IAppLogger ForContext(string sourceContext) => this;

        /// <summary>
        /// Renders the Serilog-style template the profiler emits so assertions can match on the
        /// substituted text rather than the raw placeholders.
        /// </summary>
        private static string Render(string template, object[] values)
        {
            var builder = new StringBuilder();
            var next = 0;
            for (var i = 0; i < template.Length; i++)
            {
                var close = template[i] == '{' ? template.IndexOf('}', i) : -1;
                if (close < 0 || next >= values.Length)
                {
                    builder.Append(template[i]);
                    continue;
                }

                var token = template[(i + 1)..close];
                var colon = token.IndexOf(':');
                var format = colon >= 0 ? token[(colon + 1)..] : null;
                var value = values[next++];
                builder.Append(format is null
                    ? value?.ToString()
                    : string.Format("{0:" + format + "}", value));
                i = close;
            }

            return builder.ToString();
        }
    }
}
