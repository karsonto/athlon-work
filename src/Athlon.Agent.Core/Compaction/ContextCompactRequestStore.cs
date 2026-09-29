using System.Collections.Concurrent;

namespace Athlon.Agent.Core.Compaction;

public interface IContextCompactRequestStore
{
    void Request(string sessionId);

    bool TryConsume(string sessionId);
}

public sealed class ContextCompactRequestStore : IContextCompactRequestStore
{
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);

    public void Request(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _pending[sessionId] = 0;
        }
    }

    public bool TryConsume(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _pending.TryRemove(sessionId, out _);
}
