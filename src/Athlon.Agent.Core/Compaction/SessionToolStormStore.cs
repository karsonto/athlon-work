using System.Collections.Concurrent;

namespace Athlon.Agent.Core.Compaction;

public interface ISessionToolStormStore
{
    ToolStormBreaker GetOrCreate(string sessionId, ToolStormSettings settings);

    void Reset(string sessionId);
}

public sealed class SessionToolStormStore : ISessionToolStormStore
{
    private readonly ConcurrentDictionary<string, ToolStormBreaker> _breakers = new(StringComparer.Ordinal);

    public ToolStormBreaker GetOrCreate(string sessionId, ToolStormSettings settings) =>
        _breakers.GetOrAdd(
            sessionId,
            _ => new ToolStormBreaker(settings));

    public void Reset(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _breakers.TryRemove(sessionId, out _);
        }
    }
}
