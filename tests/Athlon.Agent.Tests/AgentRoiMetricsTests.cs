using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class AgentRoiMetricsTests
{
    [Fact]
    public void Compute_reports_first_success_repeats_and_tokens_to_success()
    {
        var start = DateTimeOffset.UtcNow;
        var events = new[]
        {
            Event(start, "m1", "turn-1", AgentAttemptKind.Model, null, "success", 100, 20),
            Event(start.AddMilliseconds(1), "t1", "turn-1", AgentAttemptKind.Tool, "file_read", "success", input: "a"),
            Event(start.AddMilliseconds(2), "m2", "turn-2", AgentAttemptKind.Model, null, "success", 50, 10),
            Event(start.AddMilliseconds(3), "t2", "turn-2", AgentAttemptKind.Tool, "file_read", "failure", input: "b"),
            Event(start.AddMilliseconds(4), "m3", "turn-2", AgentAttemptKind.Model, null, "success", 30, 10),
            Event(start.AddMilliseconds(5), "t3", "turn-2", AgentAttemptKind.Tool, "file_read", "success", input: "b")
        };

        var metrics = AgentRoiMetricsCalculator.Compute(events);

        Assert.Equal(2, metrics.TurnsWithTools);
        Assert.Equal(3, metrics.ToolAttempts);
        Assert.Equal(0.5, metrics.FirstToolSuccessRate);
        Assert.Equal(1.0 / 3.0, metrics.RepeatCallRate, precision: 6);
        Assert.Equal(110, metrics.AverageTokensToSuccess);
    }

    private static AgentAttemptEvent Event(
        DateTimeOffset timestamp,
        string id,
        string turn,
        AgentAttemptKind kind,
        string? tool,
        string result,
        int prompt = 0,
        int completion = 0,
        string? input = null) =>
        new(
            timestamp, id, "session", turn, kind, ModelCallPurpose.Chat, tool, "schema", "model",
            prompt, completion, result, null, 1, InputFingerprint: input);
}
