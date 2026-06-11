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
    int TurnCount,
    DateTimeOffset LastUpdatedAt)
{
    public double? CacheHitRate =>
        CacheAvailability == PromptCacheAvailability.HitMiss
        && CacheHitTokens + CacheMissTokens > 0
            ? (double)CacheHitTokens / (CacheHitTokens + CacheMissTokens)
            : null;

    public static SessionUsageSnapshot Empty => new(
        0, 0, 0, 0, 0, PromptCacheAvailability.Unknown, 0, 0, DateTimeOffset.MinValue);
}

public interface ISessionUsageAccumulator
{
    SessionUsageSnapshot Get(string sessionId);

    SessionUsageSnapshot Record(string sessionId, ModelUsage usage, int contextSavingsTokens = 0);

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
            1,
            DateTimeOffset.UtcNow);

    private static SessionUsageSnapshot Merge(
        SessionUsageSnapshot current,
        ModelUsage usage,
        int contextSavingsTokens)
    {
        var cacheAvailability = usage.PromptCacheAvailability != PromptCacheAvailability.Unknown
            ? usage.PromptCacheAvailability
            : current.CacheAvailability;

        return new SessionUsageSnapshot(
            current.PromptTokens + (usage.PromptTokens ?? 0),
            current.CompletionTokens + (usage.CompletionTokens ?? 0),
            current.TotalTokens + (usage.TotalTokens ?? 0),
            current.CacheHitTokens + (usage.PromptCacheHitTokens ?? 0),
            current.CacheMissTokens + (usage.PromptCacheMissTokens ?? 0),
            cacheAvailability,
            current.ContextSavingsTokens + Math.Max(0, contextSavingsTokens),
            current.TurnCount + 1,
            DateTimeOffset.UtcNow);
    }
}
