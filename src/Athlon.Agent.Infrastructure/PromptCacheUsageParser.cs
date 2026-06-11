using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Parses provider-specific prompt cache fields from OpenAI-compatible usage JSON.
/// Never infers miss tokens from prompt_tokens minus cached.
/// </summary>
public static class PromptCacheUsageParser
{
    public static (
        int? PromptCacheHitTokens,
        int? PromptCacheMissTokens,
        int? CacheReadTokens,
        int? CacheCreationTokens,
        PromptCacheAvailability Availability) Parse(JsonElement usageElement)
    {
        if (usageElement.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null, null, PromptCacheAvailability.Unknown);
        }

        var nativeHit = TryInt(usageElement, "prompt_cache_hit_tokens");
        var nativeMiss = TryInt(usageElement, "prompt_cache_miss_tokens");
        if (nativeHit is not null || nativeMiss is not null)
        {
            return (
                nativeHit ?? 0,
                nativeMiss ?? 0,
                null,
                null,
                PromptCacheAvailability.HitMiss);
        }

        var cachedTokens = TryCachedTokensFromDetails(usageElement);
        if (cachedTokens is > 0)
        {
            return (cachedTokens, null, cachedTokens, null, PromptCacheAvailability.ReadOnly);
        }

        var cacheRead = TryInt(usageElement, "cache_read_input_tokens");
        var cacheCreation = TryInt(usageElement, "cache_creation_input_tokens");
        if (cacheRead is not null || cacheCreation is not null)
        {
            return (null, null, cacheRead, cacheCreation, PromptCacheAvailability.ReadOnly);
        }

        return (null, null, null, null, PromptCacheAvailability.Unknown);
    }

    public static ModelUsage MergeInto(ModelUsage usage, JsonElement usageElement)
    {
        var cache = Parse(usageElement);
        return usage with
        {
            PromptCacheHitTokens = cache.PromptCacheHitTokens,
            PromptCacheMissTokens = cache.PromptCacheMissTokens,
            CacheReadTokens = cache.CacheReadTokens,
            CacheCreationTokens = cache.CacheCreationTokens,
            PromptCacheAvailability = cache.Availability
        };
    }

    private static int? TryCachedTokensFromDetails(JsonElement usageElement)
    {
        if (!usageElement.TryGetProperty("prompt_tokens_details", out var details)
            || details.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryInt(details, "cached_tokens");
    }

    private static int? TryInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.TryGetInt64(out var longValue) && longValue is >= 0 and <= int.MaxValue)
        {
            return (int)longValue;
        }

        return null;
    }
}
