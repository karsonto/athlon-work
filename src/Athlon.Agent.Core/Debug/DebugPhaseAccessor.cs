using System.Collections.Concurrent;

namespace Athlon.Agent.Core.Debug;

public sealed class DebugPhaseAccessor : IDebugPhaseAccessor
{
    private readonly ConcurrentDictionary<string, DebugRun> _activeBySession = new(StringComparer.OrdinalIgnoreCase);

    public DebugPhase? GetPhase(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return _activeBySession.TryGetValue(sessionId, out var run) ? run.Phase : null;
    }

    public DebugRun? GetActiveRun(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return _activeBySession.TryGetValue(sessionId, out var run) ? run.Clone() : null;
    }

    public void SetActiveRun(DebugRun? run)
    {
        if (run is null || string.IsNullOrWhiteSpace(run.SessionId))
        {
            return;
        }

        _activeBySession[run.SessionId] = run.Clone();
    }

    public void Clear(string sessionId) => _activeBySession.TryRemove(sessionId, out _);
}
