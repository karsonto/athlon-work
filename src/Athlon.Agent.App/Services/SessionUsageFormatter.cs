using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

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
            parts.Add($"tokens {TokenCountDisplay.FormatCompact(snapshot.TotalTokens)} (in {TokenCountDisplay.FormatCompact(snapshot.PromptTokens)} / out {TokenCountDisplay.FormatCompact(snapshot.CompletionTokens)})");
        }

        if (snapshot.CacheAvailability == PromptCacheAvailability.HitMiss && snapshot.CacheHitRate is { } hitRate)
        {
            parts.Add($"cache {hitRate:P0}");
        }
        else if (snapshot.CacheAvailability == PromptCacheAvailability.ReadOnly && snapshot.CacheHitTokens > 0)
        {
            parts.Add($"cache read {TokenCountDisplay.FormatCompact(snapshot.CacheHitTokens)}");
        }
        if (snapshot.CacheReadTokens > 0 || snapshot.CacheCreationTokens > 0)
        {
            parts.Add($"cache io {TokenCountDisplay.FormatCompact(snapshot.CacheReadTokens)} read / {TokenCountDisplay.FormatCompact(snapshot.CacheCreationTokens)} create");
        }

        if (snapshot.HygieneSavingsTokens > 0)
        {
            parts.Add($"saved ~{TokenCountDisplay.FormatCompact(snapshot.HygieneSavingsTokens)} (hygiene)");
        }

        if (snapshot.CompactionSavingsTokens > 0)
        {
            parts.Add($"compact ~{TokenCountDisplay.FormatCompact(snapshot.CompactionSavingsTokens)}");
        }

        if (snapshot.SubAgentRollupPromptTokens + snapshot.SubAgentRollupCompletionTokens > 0)
        {
            parts.Add($"incl. sub-agents {TokenCountDisplay.FormatCompact(snapshot.SubAgentRollupPromptTokens + snapshot.SubAgentRollupCompletionTokens)}");
        }

        return string.Join(" · ", parts);
    }
}
