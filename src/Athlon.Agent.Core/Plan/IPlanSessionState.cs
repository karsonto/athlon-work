namespace Athlon.Agent.Core.Plan;

public sealed class PlanRunChangedEventArgs(PlanRun? run) : EventArgs
{
    public PlanRun? Run { get; } = run?.Clone();
}

public interface IPlanSessionState
{
    event EventHandler<PlanRunChangedEventArgs>? RunChanged;

    PlanRun? GetActiveRun(string sessionId);

    void NotifyChanged(PlanRun? run);
}
