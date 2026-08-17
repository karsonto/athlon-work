namespace Athlon.Agent.Core.Debug;

public sealed class DebugRunChangedEventArgs(DebugRun? run) : EventArgs
{
    public DebugRun? Run { get; } = run?.Clone();
}

public interface IDebugSessionState
{
    event EventHandler<DebugRunChangedEventArgs>? RunChanged;

    DebugRun? GetActiveRun(string sessionId);

    void NotifyChanged(DebugRun? run);
}
