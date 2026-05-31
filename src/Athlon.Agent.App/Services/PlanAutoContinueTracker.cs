using System.Collections.Concurrent;

namespace Athlon.Agent.App.Services;

public sealed class PlanAutoContinueTracker
{
    private readonly ConcurrentDictionary<string, int> _completedRounds = new(StringComparer.Ordinal);

    public int Get(string sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? 0 : _completedRounds.GetValueOrDefault(sessionId);

    public int Increment(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return 0;
        }

        return _completedRounds.AddOrUpdate(sessionId, 1, static (_, current) => current + 1);
    }

    public void Reset(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _completedRounds.TryRemove(sessionId, out _);
        }
    }
}
