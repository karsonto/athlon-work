namespace Athlon.Agent.Core.Debug;

public interface IDebugPhaseAccessor
{
    DebugPhase? GetPhase(string? sessionId);

    DebugRun? GetActiveRun(string? sessionId);

    void SetActiveRun(DebugRun? run);

    void Clear(string sessionId);
}
