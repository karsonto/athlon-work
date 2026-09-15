using System.Collections.Concurrent;

namespace Athlon.Agent.Core.Plan;

/// <summary>
/// Tracks the auto-continuation budget for an executing plan.
///
/// <para>Kept separate from the continuation service so the artifact clearer can reset a session's
/// budget without depending on the UI-layer service.</para>
/// </summary>
public interface IPlanContinuationTracker
{
    /// <summary>Clears the session's round counter and stop flag. Called on each Build.</summary>
    void Reset(string sessionId);

    /// <summary>Registers one auto-continuation round and returns the new count.</summary>
    int Increment(string sessionId);

    /// <summary>Number of consecutive auto-continuation rounds used since the last reset.</summary>
    int GetCount(string sessionId);

    /// <summary>Marks the session as stopped so no further auto-continuation happens until the next reset.</summary>
    void Stop(string sessionId);

    /// <summary>True when the user stopped auto-continuation for this session.</summary>
    bool IsStopped(string sessionId);
}

/// <summary>In-memory <see cref="IPlanContinuationTracker"/>. Counters are intentionally not persisted.</summary>
public sealed class PlanContinuationTracker : IPlanContinuationTracker
{
    private readonly ConcurrentDictionary<string, int> _rounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _stopped = new(StringComparer.OrdinalIgnoreCase);

    public void Reset(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _rounds.TryRemove(sessionId, out _);
        _stopped.TryRemove(sessionId, out _);
    }

    public int Increment(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return 0;
        }

        return _rounds.AddOrUpdate(sessionId, 1, (_, current) => current + 1);
    }

    public int GetCount(string sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) || !_rounds.TryGetValue(sessionId, out var count) ? 0 : count;

    public void Stop(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _stopped[sessionId] = 0;
    }

    public bool IsStopped(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _stopped.ContainsKey(sessionId);
}
