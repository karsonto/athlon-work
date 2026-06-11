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
        Assert.Equal(75, snapshot.HygieneSavingsTokens);
    }

    [Fact]
    public void RecordRollup_accumulates_sub_agent_tokens_on_parent()
    {
        var accumulator = new SessionUsageAccumulator();
        accumulator.RecordRollup("parent", new ModelUsage(40, 10, 50), hygieneSavingsTokens: 5);
        var snapshot = accumulator.RecordRollup("parent", new ModelUsage(20, 5, 25), hygieneSavingsTokens: 3);

        Assert.Equal(60, snapshot.SubAgentRollupPromptTokens);
        Assert.Equal(15, snapshot.SubAgentRollupCompletionTokens);
        Assert.Equal(8, snapshot.HygieneSavingsTokens);
        Assert.Equal(0, snapshot.TurnCount);
    }

    [Fact]
    public void RecordCompaction_tracks_compaction_savings()
    {
        var accumulator = new SessionUsageAccumulator();
        var snapshot = accumulator.RecordCompaction("s1", tokensBefore: 10_000, tokensAfter: 4_000);

        Assert.Equal(6_000, snapshot.CompactionSavingsTokens);
        Assert.Equal(6_000, snapshot.ContextSavingsTokens);
    }
}
