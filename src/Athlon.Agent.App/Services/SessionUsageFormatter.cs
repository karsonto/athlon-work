using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

internal static class SessionUsageFormatter
{
    public static string Format(SessionUsageSnapshot snapshot)
    {
        if (snapshot.TurnCount <= 0)
        {
            return string.Empty;
        }

        var parts = new List<string>
        {
            $"tokens {FormatCompact(snapshot.TotalTokens)} (in {FormatCompact(snapshot.PromptTokens)} / out {FormatCompact(snapshot.CompletionTokens)})"
        };

        if (snapshot.CacheAvailability == PromptCacheAvailability.HitMiss && snapshot.CacheHitRate is { } hitRate)
        {
            parts.Add($"cache {hitRate:P0}");
        }
        else if (snapshot.CacheAvailability == PromptCacheAvailability.ReadOnly && snapshot.CacheHitTokens > 0)
        {
            parts.Add($"cache read {FormatCompact(snapshot.CacheHitTokens)}");
        }

        if (snapshot.ContextSavingsTokens > 0)
        {
            parts.Add($"saved ~{FormatCompact(snapshot.ContextSavingsTokens)}");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatCompact(int value) =>
        value >= 1_000_000 ? $"{value / 1_000_000.0:F1}M"
        : value >= 1_000 ? $"{value / 1_000.0:F1}K"
        : value.ToString();
}
