using Athlon.Agent.App.Services.Streaming;

namespace Athlon.Agent.Tests;

public sealed class AssistantMarkdownCoalesceGateTests
{
    [Fact]
    public void TryBegin_skips_unchanged_streaming_content()
    {
        var gate = new AssistantMarkdownCoalesceGate();

        Assert.Equal(CoalesceDecision.PostNow, gate.TryBegin("m1", "hello", streaming: true));
        gate.Complete();
        Assert.Equal(CoalesceDecision.SkipUnchanged, gate.TryBegin("m1", "hello", streaming: true));
        Assert.False(gate.IsInFlight);
    }

    [Fact]
    public void TryBegin_queues_while_in_flight_and_allows_follow_up()
    {
        var gate = new AssistantMarkdownCoalesceGate();

        Assert.Equal(CoalesceDecision.PostNow, gate.TryBegin("m1", "a", streaming: true));
        Assert.True(gate.IsInFlight);
        Assert.Equal(CoalesceDecision.Queued, gate.TryBegin("m1", "ab", streaming: true));
        Assert.True(gate.IsInFlight);

        gate.Complete();
        Assert.False(gate.IsInFlight);
        Assert.Equal(CoalesceDecision.PostNow, gate.TryBegin("m1", "ab", streaming: true));
        Assert.Equal("ab", gate.LastPostedContent);
    }

    [Fact]
    public void Finalize_is_never_skipped_as_unchanged()
    {
        var gate = new AssistantMarkdownCoalesceGate();

        Assert.Equal(CoalesceDecision.PostNow, gate.TryBegin("m1", "final", streaming: true));
        gate.Complete();
        Assert.Equal(CoalesceDecision.PostNow, gate.TryBegin("m1", "final", streaming: false));
        Assert.True(gate.IsInFlight);
    }

    [Fact]
    public void Finalize_while_in_flight_is_queued()
    {
        var gate = new AssistantMarkdownCoalesceGate();

        Assert.Equal(CoalesceDecision.PostNow, gate.TryBegin("m1", "partial", streaming: true));
        Assert.Equal(CoalesceDecision.Queued, gate.TryBegin("m1", "final", streaming: false));
        gate.Complete();
        Assert.Equal(CoalesceDecision.PostNow, gate.TryBegin("m1", "final", streaming: false));
    }
}
