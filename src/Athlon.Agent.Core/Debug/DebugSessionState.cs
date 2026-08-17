namespace Athlon.Agent.Core.Debug;

public sealed class DebugSessionState : IDebugSessionState
{
    public event EventHandler<DebugRunChangedEventArgs>? RunChanged;

    private DebugRun? _latest;

    public DebugRun? GetActiveRun(string sessionId) =>
        _latest is not null && string.Equals(_latest.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
            ? _latest.Clone()
            : null;

    public void NotifyChanged(DebugRun? run)
    {
        _latest = run?.Clone();
        RunChanged?.Invoke(this, new DebugRunChangedEventArgs(run));
    }
}
