namespace Athlon.Agent.Core.Plan;

public sealed class PlanSessionState : IPlanSessionState
{
    public event EventHandler<PlanRunChangedEventArgs>? RunChanged;

    private PlanRun? _latest;

    public PlanRun? GetActiveRun(string sessionId) =>
        _latest is not null && string.Equals(_latest.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
            ? _latest.Clone()
            : null;

    public void NotifyChanged(PlanRun? run)
    {
        _latest = run?.Clone();
        RunChanged?.Invoke(this, new PlanRunChangedEventArgs(run));
    }
}
