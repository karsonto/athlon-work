using System.Collections.Concurrent;

namespace Athlon.Agent.Core.Compaction;

public interface IPromptPressureStore
{
    int? GetLastPromptTokens(string sessionId);

    void Record(string sessionId, int promptTokens);
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
}
