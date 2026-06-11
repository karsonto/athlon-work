using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionUsageAccumulatorTests
{
    [Fact]
    public void Record_accumulates_tokens_and_context_savings()
    {
        var accumulator = new SessionUsageAccumulator();
        accumulator.Record("s1", new ModelUsage(100, 20, 120, 80, 20, null, null, PromptCacheAvailability.HitMiss), 50);
        var snapshot = accumulator.Record("s1", new ModelUsage(50, 10, 60), 25);

        Assert.Equal(150, snapshot.PromptTokens);
        Assert.Equal(180, snapshot.TotalTokens);
        Assert.Equal(75, snapshot.ContextSavingsTokens);
        Assert.Equal(2, snapshot.TurnCount);
        Assert.Equal(0.8, snapshot.CacheHitRate);
    }
}
