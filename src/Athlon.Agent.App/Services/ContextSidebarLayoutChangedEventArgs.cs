namespace Athlon.Agent.App.Services;

public sealed class ContextSidebarLayoutChangedEventArgs : EventArgs
{
    public bool Animate { get; init; }
}
