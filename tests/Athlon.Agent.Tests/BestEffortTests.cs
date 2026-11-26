using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class BestEffortTests
{
    [Fact]
    public void Ignore_SwallowsException()
    {
        BestEffort.Ignore(() => throw new InvalidOperationException("x"));
    }

    [Fact]
    public async Task IgnoreAsync_SwallowsException()
    {
        await BestEffort.IgnoreAsync(() => throw new InvalidOperationException("x"));
    }

    [Fact]
    public void Ignore_LogsDebugWhenLoggerPresent()
    {
        var logger = new CapturingLogger();
        BestEffort.Ignore(() => throw new InvalidOperationException("boom"), logger, "teardown");
        Assert.Contains(logger.Messages, m => m.Contains("teardown", StringComparison.Ordinal)
            && m.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public void LogAndContinue_WritesError()
    {
        var logger = new CapturingLogger();
        var ex = new InvalidOperationException("boom");
        BestEffort.LogAndContinue(ex, logger, "side effect failed for {Id}", "abc");
        Assert.Contains(logger.Errors, e => ReferenceEquals(e.Exception, ex));
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public List<string> Messages { get; } = [];
        public List<(Exception Exception, string Template)> Errors { get; } = [];

        public void Debug(string messageTemplate, params object[] values) =>
            Messages.Add(Format(messageTemplate, values));

        public void Information(string messageTemplate, params object[] values) =>
            Messages.Add(Format(messageTemplate, values));

        public void Warning(string messageTemplate, params object[] values) =>
            Messages.Add(Format(messageTemplate, values));

        public void Error(Exception exception, string messageTemplate, params object[] values) =>
            Errors.Add((exception, messageTemplate));

        public IAppLogger ForContext(string sourceContext) => this;

        private static string Format(string template, object[] values)
        {
            var text = template;
            foreach (var value in values)
            {
                text += " " + value;
            }

            return text;
        }
    }
}
