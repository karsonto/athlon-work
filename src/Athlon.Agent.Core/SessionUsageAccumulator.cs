using System.Collections.Concurrent;

namespace Athlon.Agent.Core;

public enum ModelCallPurpose
{
    Chat,
    Summary,
    Memory,
    SubAgent
}

public sealed record PurposeUsageSnapshot(
    int Calls,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int CacheReadTokens,
    int CacheCreationTokens);

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
    public int CacheReadTokens { get; init; }
    public int CacheCreationTokens { get; init; }
    public IReadOnlyDictionary<ModelCallPurpose, PurposeUsageSnapshot> ByPurpose { get; init; } =
        new Dictionary<ModelCallPurpose, PurposeUsageSnapshot>();

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

    SessionUsageSnapshot RecordCall(
        string sessionId,
        string callId,
        ModelCallPurpose purpose,
        ModelUsage usage,
        int contextSavingsTokens = 0,
        bool subAgentRollup = false);

    SessionUsageSnapshot RecordCompaction(string sessionId, int tokensBefore, int tokensAfter);

    void Reset(string sessionId);
}

public sealed class SessionUsageAccumulator : ISessionUsageAccumulator
{
    private readonly ConcurrentDictionary<string, SessionUsageSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _recordedCalls = new(StringComparer.Ordinal);

    public SessionUsageSnapshot Get(string sessionId) =>
        _snapshots.GetValueOrDefault(sessionId) ?? SessionUsageSnapshot.Empty;

    public SessionUsageSnapshot Record(string sessionId, ModelUsage usage, int contextSavingsTokens = 0)
        => RecordCall(sessionId, Guid.NewGuid().ToString("N"), ModelCallPurpose.Chat, usage, contextSavingsTokens);

    public SessionUsageSnapshot RecordCall(
        string sessionId,
        string callId,
        ModelCallPurpose purpose,
        ModelUsage usage,
        int contextSavingsTokens = 0,
        bool subAgentRollup = false)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return SessionUsageSnapshot.Empty;
        }
        if (!_recordedCalls.TryAdd($"{sessionId}\n{callId}", 0))
        {
            return Get(sessionId);
        }

        return _snapshots.AddOrUpdate(
            sessionId,
            _ => CreateSnapshot(usage, purpose, contextSavingsTokens, subAgentRollup),
            (_, current) => Merge(current, usage, purpose, contextSavingsTokens, subAgentRollup));
    }

    public SessionUsageSnapshot RecordRollup(string parentSessionId, ModelUsage usage, int hygieneSavingsTokens = 0)
    {
        if (string.IsNullOrWhiteSpace(parentSessionId))
        {
            return SessionUsageSnapshot.Empty;
        }

        return RecordCall(
            parentSessionId,
            Guid.NewGuid().ToString("N"),
            ModelCallPurpose.SubAgent,
            usage,
            hygieneSavingsTokens,
            subAgentRollup: true);
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
            foreach (var key in _recordedCalls.Keys.Where(key => key.StartsWith(sessionId + "\n", StringComparison.Ordinal)))
            {
                _recordedCalls.TryRemove(key, out _);
            }
        }
    }

    private static SessionUsageSnapshot CreateSnapshot(
        ModelUsage usage,
        ModelCallPurpose purpose,
        int contextSavingsTokens,
        bool subAgentRollup) =>
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
            subAgentRollup ? usage.PromptTokens ?? 0 : 0,
            subAgentRollup ? usage.CompletionTokens ?? 0 : 0,
            subAgentRollup ? 0 : 1,
            DateTimeOffset.UtcNow)
        {
            CacheReadTokens = usage.CacheReadTokens ?? 0,
            CacheCreationTokens = usage.CacheCreationTokens ?? 0,
            ByPurpose = new Dictionary<ModelCallPurpose, PurposeUsageSnapshot>
            {
                [purpose] = CreatePurpose(usage)
            }
        };

    private static SessionUsageSnapshot Merge(
        SessionUsageSnapshot current,
        ModelUsage usage,
        ModelCallPurpose purpose,
        int contextSavingsTokens,
        bool subAgentRollup)
    {
        var cacheAvailability = usage.PromptCacheAvailability != PromptCacheAvailability.Unknown
            ? usage.PromptCacheAvailability
            : current.CacheAvailability;
        var hygieneSavings = Math.Max(0, contextSavingsTokens);

        var updated = new SessionUsageSnapshot(
            current.PromptTokens + (usage.PromptTokens ?? 0),
            current.CompletionTokens + (usage.CompletionTokens ?? 0),
            current.TotalTokens + (usage.TotalTokens ?? 0),
            current.CacheHitTokens + (usage.PromptCacheHitTokens ?? 0),
            current.CacheMissTokens + (usage.PromptCacheMissTokens ?? 0),
            cacheAvailability,
            current.ContextSavingsTokens + hygieneSavings,
            current.HygieneSavingsTokens + hygieneSavings,
            current.CompactionSavingsTokens,
            current.SubAgentRollupPromptTokens + (subAgentRollup ? usage.PromptTokens ?? 0 : 0),
            current.SubAgentRollupCompletionTokens + (subAgentRollup ? usage.CompletionTokens ?? 0 : 0),
            current.TurnCount + (subAgentRollup ? 0 : 1),
            DateTimeOffset.UtcNow)
        {
            CacheReadTokens = current.CacheReadTokens + (usage.CacheReadTokens ?? 0),
            CacheCreationTokens = current.CacheCreationTokens + (usage.CacheCreationTokens ?? 0),
            ByPurpose = MergePurpose(current.ByPurpose, purpose, usage)
        };
        return updated;
    }

    private static PurposeUsageSnapshot CreatePurpose(ModelUsage usage) =>
        new(
            1,
            usage.PromptTokens ?? 0,
            usage.CompletionTokens ?? 0,
            usage.TotalTokens ?? ((usage.PromptTokens ?? 0) + (usage.CompletionTokens ?? 0)),
            usage.CacheReadTokens ?? 0,
            usage.CacheCreationTokens ?? 0);

    private static IReadOnlyDictionary<ModelCallPurpose, PurposeUsageSnapshot> MergePurpose(
        IReadOnlyDictionary<ModelCallPurpose, PurposeUsageSnapshot> current,
        ModelCallPurpose purpose,
        ModelUsage usage)
    {
        var output = new Dictionary<ModelCallPurpose, PurposeUsageSnapshot>(current);
        var addition = CreatePurpose(usage);
        output.TryGetValue(purpose, out var existing);
        output[purpose] = existing is null
            ? addition
            : new PurposeUsageSnapshot(
                existing.Calls + 1,
                existing.PromptTokens + addition.PromptTokens,
                existing.CompletionTokens + addition.CompletionTokens,
                existing.TotalTokens + addition.TotalTokens,
                existing.CacheReadTokens + addition.CacheReadTokens,
                existing.CacheCreationTokens + addition.CacheCreationTokens);
        return output;
    }
}
