using System.Collections.Concurrent;

namespace Athlon.Agent.Core;

public sealed record SessionUsageSnapshot(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int CacheHitTokens,
    int CacheMissTokens,
    PromptCacheAvailability CacheAvailability,
    int ContextSavingsTokens,
    int HygieneSavingsTokens,
    int CompactionSavingsTokens,
    int SubAgentRollupPromptTokens,
    int SubAgentRollupCompletionTokens,
    int TurnCount,
    DateTimeOffset LastUpdatedAt)
{
    public double? CacheHitRate =>
        CacheAvailability == PromptCacheAvailability.HitMiss
        && CacheHitTokens + CacheMissTokens > 0
            ? (double)CacheHitTokens / (CacheHitTokens + CacheMissTokens)
            : null;

    public static SessionUsageSnapshot Empty => new(
        0, 0, 0, 0, 0, PromptCacheAvailability.Unknown, 0, 0, 0, 0, 0, 0, DateTimeOffset.MinValue);
}

public interface ISessionUsageAccumulator
{
    SessionUsageSnapshot Get(string sessionId);

    SessionUsageSnapshot Record(string sessionId, ModelUsage usage, int contextSavingsTokens = 0);

    SessionUsageSnapshot RecordRollup(string parentSessionId, ModelUsage usage, int hygieneSavingsTokens = 0);

    SessionUsageSnapshot RecordCompaction(string sessionId, int tokensBefore, int tokensAfter);

    void Reset(string sessionId);
}

public sealed class SessionUsageAccumulator : ISessionUsageAccumulator
{
    private readonly ConcurrentDictionary<string, SessionUsageSnapshot> _snapshots = new(StringComparer.Ordinal);

    public SessionUsageSnapshot Get(string sessionId) =>
        _snapshots.GetValueOrDefault(sessionId) ?? SessionUsageSnapshot.Empty;

    public SessionUsageSnapshot Record(string sessionId, ModelUsage usage, int contextSavingsTokens = 0)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return SessionUsageSnapshot.Empty;
        }

        return _snapshots.AddOrUpdate(
            sessionId,
            _ => CreateSnapshot(usage, contextSavingsTokens),
            (_, current) => Merge(current, usage, contextSavingsTokens));
    }

    public SessionUsageSnapshot RecordRollup(string parentSessionId, ModelUsage usage, int hygieneSavingsTokens = 0)
    {
        if (string.IsNullOrWhiteSpace(parentSessionId))
        {
            return SessionUsageSnapshot.Empty;
        }

        return _snapshots.AddOrUpdate(
            parentSessionId,
            _ => CreateRollupSnapshot(usage, hygieneSavingsTokens),
            (_, current) => MergeRollup(current, usage, hygieneSavingsTokens));
    }

    public SessionUsageSnapshot RecordCompaction(string sessionId, int tokensBefore, int tokensAfter)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return SessionUsageSnapshot.Empty;
        }

        var savings = Math.Max(0, tokensBefore - tokensAfter);
        return _snapshots.AddOrUpdate(
            sessionId,
            _ => new SessionUsageSnapshot(
                0, 0, 0, 0, 0, PromptCacheAvailability.Unknown, savings, 0, savings, 0, 0, 0, DateTimeOffset.UtcNow),
            (_, current) => current with
            {
                CompactionSavingsTokens = current.CompactionSavingsTokens + savings,
                ContextSavingsTokens = current.ContextSavingsTokens + savings,
                LastUpdatedAt = DateTimeOffset.UtcNow
            });
    }

    public void Reset(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _snapshots.TryRemove(sessionId, out _);
        }
    }

    private static SessionUsageSnapshot CreateSnapshot(ModelUsage usage, int contextSavingsTokens) =>
        new(
            usage.PromptTokens ?? 0,
            usage.CompletionTokens ?? 0,
            usage.TotalTokens ?? 0,
            usage.PromptCacheHitTokens ?? 0,
            usage.PromptCacheMissTokens ?? 0,
            usage.PromptCacheAvailability,
            Math.Max(0, contextSavingsTokens),
            Math.Max(0, contextSavingsTokens),
            0,
            0,
            0,
            1,
            DateTimeOffset.UtcNow);

    private static SessionUsageSnapshot CreateRollupSnapshot(ModelUsage usage, int hygieneSavingsTokens) =>
        new(
            0,
            0,
            0,
            0,
            0,
            PromptCacheAvailability.Unknown,
            Math.Max(0, hygieneSavingsTokens),
            Math.Max(0, hygieneSavingsTokens),
            0,
            usage.PromptTokens ?? 0,
            usage.CompletionTokens ?? 0,
            0,
            DateTimeOffset.UtcNow);

    private static SessionUsageSnapshot Merge(
        SessionUsageSnapshot current,
        ModelUsage usage,
        int contextSavingsTokens)
    {
        var cacheAvailability = usage.PromptCacheAvailability != PromptCacheAvailability.Unknown
            ? usage.PromptCacheAvailability
            : current.CacheAvailability;
        var hygieneSavings = Math.Max(0, contextSavingsTokens);

        return new SessionUsageSnapshot(
            current.PromptTokens + (usage.PromptTokens ?? 0),
            current.CompletionTokens + (usage.CompletionTokens ?? 0),
            current.TotalTokens + (usage.TotalTokens ?? 0),
            current.CacheHitTokens + (usage.PromptCacheHitTokens ?? 0),
            current.CacheMissTokens + (usage.PromptCacheMissTokens ?? 0),
            cacheAvailability,
            current.ContextSavingsTokens + hygieneSavings,
            current.HygieneSavingsTokens + hygieneSavings,
            current.CompactionSavingsTokens,
            current.SubAgentRollupPromptTokens,
            current.SubAgentRollupCompletionTokens,
            current.TurnCount + 1,
            DateTimeOffset.UtcNow);
    }

    private static SessionUsageSnapshot MergeRollup(
        SessionUsageSnapshot current,
        ModelUsage usage,
        int hygieneSavingsTokens)
    {
        var hygieneSavings = Math.Max(0, hygieneSavingsTokens);
        return current with
        {
            ContextSavingsTokens = current.ContextSavingsTokens + hygieneSavings,
            HygieneSavingsTokens = current.HygieneSavingsTokens + hygieneSavings,
            SubAgentRollupPromptTokens = current.SubAgentRollupPromptTokens + (usage.PromptTokens ?? 0),
            SubAgentRollupCompletionTokens = current.SubAgentRollupCompletionTokens + (usage.CompletionTokens ?? 0),
            LastUpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
