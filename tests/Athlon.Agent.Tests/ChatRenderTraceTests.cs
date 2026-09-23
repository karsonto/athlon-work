using Athlon.Agent.App.Services.Diagnostics;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

/// <summary>
/// Covers <see cref="ChatRenderTrace"/>, the observability added to locate "content sometimes only
/// appears after switching session".
///
/// <para>The property under test is deliberate: this trace exists to count how <em>often</em> a
/// render gate fires. The user-visible report is "occasionally", so a trace that logs the first
/// occurrence and then goes quiet (which is what the previous <c>_loggedCanRenderBlock</c> flag did)
/// cannot answer the question it was added for. Every test therefore checks the running counter, not
/// only that a line was emitted.</para>
/// </summary>
public sealed class ChatRenderTraceTests
{
    [Fact]
    public void Record_counts_every_occurrence_not_just_the_first()
    {
        var logger = new RecordingLogger();
        ChatRenderTrace.Initialize(logger);
        ChatRenderTrace.ResetCounts();

        ChatRenderTrace.Record("canRenderFalse", "w=2 h=2");
        ChatRenderTrace.Record("canRenderFalse", "w=2 h=2");
        ChatRenderTrace.Record("canRenderFalse", "w=2 h=2");

        Assert.Equal(3, ChatRenderTrace.CountOf("canRenderFalse"));

        // The count is what makes a repeated gate visible; it must be on the log line too, because
        // the log is the only place the number is actually read.
        Assert.Contains("#=1", logger.Lines[0]);
        Assert.Contains("#=3", logger.Lines[2]);
    }

    [Fact]
    public void Record_prefixes_lines_so_they_are_greppable_apart_from_switch_events()
    {
        var logger = new RecordingLogger();
        ChatRenderTrace.Initialize(logger);
        ChatRenderTrace.ResetCounts();

        ChatRenderTrace.Record("orphan", "gen=7 retry=False");

        var line = Assert.Single(logger.Lines);
        Assert.StartsWith("chat.render.orphan", line);
        Assert.Contains("gen=7", line);
    }

    [Fact]
    public void Record_without_details_still_emits_a_counted_line()
    {
        var logger = new RecordingLogger();
        ChatRenderTrace.Initialize(logger);
        ChatRenderTrace.ResetCounts();

        // A kind whose occurrence alone is the signal (e.g. messagesRefilled) must not require a
        // fabricated detail string to be observable.
        ChatRenderTrace.Record("messagesRefilled");

        Assert.Equal(1, ChatRenderTrace.CountOf("messagesRefilled"));
        Assert.Contains("#=1", Assert.Single(logger.Lines));
    }

    [Fact]
    public void Disabled_trace_does_not_emit_or_count()
    {
        var logger = new RecordingLogger();
        ChatRenderTrace.Initialize(logger, enabled: false);
        ChatRenderTrace.ResetCounts();

        ChatRenderTrace.Record("orphan", "gen=1");

        Assert.Empty(logger.Lines);
        Assert.Equal(0, ChatRenderTrace.CountOf("orphan"));
    }

    [Fact]
    public void Empty_kind_is_ignored()
    {
        var logger = new RecordingLogger();
        ChatRenderTrace.Initialize(logger);
        ChatRenderTrace.ResetCounts();

        ChatRenderTrace.Record("");

        Assert.Empty(logger.Lines);
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Lines { get; } = [];

        public void Debug(string messageTemplate, params object[] values) => Add(messageTemplate, values);

        public void Information(string messageTemplate, params object[] values) => Add(messageTemplate, values);

        public void Warning(string messageTemplate, params object[] values) => Add(messageTemplate, values);

        public void Error(Exception exception, string messageTemplate, params object[] values) =>
            Add(messageTemplate, values);

        public IAppLogger ForContext(string sourceContext) => this;

        private void Add(string template, object[] values)
        {
            // ChatRenderTrace logs "{Line}" with the preformatted message as the single argument.
            Lines.Add(values.Length == 1 ? values[0]?.ToString() ?? "" : string.Format(template, values));
        }
    }
}
