using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

internal static class SessionUsageFormatter
{
    public static string Format(SessionUsageSnapshot snapshot)
    {
        if (snapshot.TurnCount <= 0 && snapshot.SubAgentRollupPromptTokens <= 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (snapshot.TurnCount > 0)
        {
            parts.Add($"tokens {FormatCompact(snapshot.TotalTokens)} (in {FormatCompact(snapshot.PromptTokens)} / out {FormatCompact(snapshot.CompletionTokens)})");
        }

        if (snapshot.CacheAvailability == PromptCacheAvailability.HitMiss && snapshot.CacheHitRate is { } hitRate)
        {
            parts.Add($"cache {hitRate:P0}");
        }
        else if (snapshot.CacheAvailability == PromptCacheAvailability.ReadOnly && snapshot.CacheHitTokens > 0)
        {
            parts.Add($"cache read {FormatCompact(snapshot.CacheHitTokens)}");
        }
        if (snapshot.CacheReadTokens > 0 || snapshot.CacheCreationTokens > 0)
        {
            parts.Add($"cache io {FormatCompact(snapshot.CacheReadTokens)} read / {FormatCompact(snapshot.CacheCreationTokens)} create");
        }

        if (snapshot.HygieneSavingsTokens > 0)
        {
            parts.Add($"saved ~{FormatCompact(snapshot.HygieneSavingsTokens)} (hygiene)");
        }

        if (snapshot.CompactionSavingsTokens > 0)
        {
            parts.Add($"compact ~{FormatCompact(snapshot.CompactionSavingsTokens)}");
        }

        if (snapshot.SubAgentRollupPromptTokens + snapshot.SubAgentRollupCompletionTokens > 0)
        {
            parts.Add($"incl. sub-agents {FormatCompact(snapshot.SubAgentRollupPromptTokens + snapshot.SubAgentRollupCompletionTokens)}");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatCompact(int value) =>
        value >= 1_000_000 ? $"{value / 1_000_000.0:F1}M"
        : value >= 1_000 ? $"{value / 1_000.0:F1}K"
        : value.ToString();
}
