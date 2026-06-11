using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class PromptCacheUsageParserTests
{
    [Fact]
    public void Parse_deepseek_native_hit_miss()
    {
        using var doc = JsonDocument.Parse("""{"prompt_cache_hit_tokens":8000,"prompt_cache_miss_tokens":2000}""");
        var result = PromptCacheUsageParser.Parse(doc.RootElement);

        Assert.Equal(8000, result.PromptCacheHitTokens);
        Assert.Equal(2000, result.PromptCacheMissTokens);
        Assert.Equal(PromptCacheAvailability.HitMiss, result.Availability);
    }

    [Fact]
    public void Parse_openai_cached_tokens_read_only()
    {
        using var doc = JsonDocument.Parse("""{"prompt_tokens_details":{"cached_tokens":4096}}""");
        var result = PromptCacheUsageParser.Parse(doc.RootElement);

        Assert.Equal(4096, result.CacheReadTokens);
        Assert.Null(result.PromptCacheMissTokens);
        Assert.Equal(PromptCacheAvailability.ReadOnly, result.Availability);
    }

    [Fact]
    public void Parse_anthropic_cache_read_creation()
    {
        using var doc = JsonDocument.Parse("""{"cache_read_input_tokens":3000,"cache_creation_input_tokens":500}""");
        var result = PromptCacheUsageParser.Parse(doc.RootElement);

        Assert.Equal(3000, result.CacheReadTokens);
        Assert.Equal(500, result.CacheCreationTokens);
        Assert.Equal(PromptCacheAvailability.ReadOnly, result.Availability);
    }

    [Fact]
    public void Parse_empty_usage_unknown()
    {
        using var doc = JsonDocument.Parse("{}");
        var result = PromptCacheUsageParser.Parse(doc.RootElement);

        Assert.Equal(PromptCacheAvailability.Unknown, result.Availability);
    }

    [Fact]
    public void MergeInto_computes_hit_rate_only_for_hit_miss()
    {
        using var doc = JsonDocument.Parse("""{"prompt_tokens":10000,"prompt_cache_hit_tokens":7500,"prompt_cache_miss_tokens":2500}""");
        var usage = PromptCacheUsageParser.MergeInto(new ModelUsage(10000, 500, 10500), doc.RootElement);

        Assert.Equal(PromptCacheAvailability.HitMiss, usage.PromptCacheAvailability);
        Assert.Equal(0.75, usage.PromptCacheHitRate);
    }

    [Fact]
    public void MergeInto_read_only_has_no_hit_rate()
    {
        using var doc = JsonDocument.Parse("""{"prompt_tokens_details":{"cached_tokens":1000}}""");
        var usage = PromptCacheUsageParser.MergeInto(new ModelUsage(5000, 100, 5100), doc.RootElement);

        Assert.Equal(PromptCacheAvailability.ReadOnly, usage.PromptCacheAvailability);
        Assert.Null(usage.PromptCacheHitRate);
    }
}
