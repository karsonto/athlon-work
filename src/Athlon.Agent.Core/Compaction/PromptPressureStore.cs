using System.Collections.Concurrent;

namespace Athlon.Agent.Core.Compaction;

public interface IPromptPressureStore
{
    int? GetLastPromptTokens(string sessionId);

    void Record(string sessionId, int promptTokens);

    /// <summary>
    /// Drops the stored measurement for <paramref name="sessionId"/>. Called after a compaction
    /// rewrote the payload and after the context is cleared: the stored value describes a payload
    /// that no longer exists, so keeping it would inflate the next budget back to pre-compaction
    /// levels and make utilization appear not to fall.
    /// </summary>
    void Clear(string sessionId);
}

public sealed class PromptPressureStore : IPromptPressureStore
{
    private readonly ConcurrentDictionary<string, int> _lastPromptTokens = new(StringComparer.Ordinal);

    public int? GetLastPromptTokens(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return _lastPromptTokens.TryGetValue(sessionId, out var tokens) ? tokens : null;
    }

    public void Record(string sessionId, int promptTokens)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || promptTokens <= 0)
        {
            return;
        }

        // Store the latest actual prompt size so pressure can fall after compaction.
        _lastPromptTokens[sessionId] = promptTokens;
    }

    public void Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _lastPromptTokens.TryRemove(sessionId, out _);
    }
}
