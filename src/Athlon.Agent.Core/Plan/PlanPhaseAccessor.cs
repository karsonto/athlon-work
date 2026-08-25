using System.Collections.Concurrent;

namespace Athlon.Agent.Core.Plan;

public sealed class PlanPhaseAccessor : IPlanPhaseAccessor
{
    private readonly ConcurrentDictionary<string, PlanRun> _activeBySession = new(StringComparer.OrdinalIgnoreCase);

    public PlanPhase? GetPhase(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return _activeBySession.TryGetValue(sessionId, out var run) ? run.Phase : null;
    }

    public PlanRun? GetActiveRun(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return _activeBySession.TryGetValue(sessionId, out var run) ? run.Clone() : null;
    }

    public void SetActiveRun(PlanRun? run)
    {
        if (run is null || string.IsNullOrWhiteSpace(run.SessionId))
        {
            return;
        }

        _activeBySession[run.SessionId] = run.Clone();
    }

    public void Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _activeBySession.TryRemove(sessionId, out _);
    }
}
