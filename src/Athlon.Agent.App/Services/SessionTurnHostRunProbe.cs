using Athlon.Agent.Core.Cli;

namespace Athlon.Agent.App.Services;

internal sealed class SessionTurnHostRunProbe(SessionTurnHost host) : IDesktopSessionRunProbe
{
    public bool IsSessionRunning(string sessionId) => host.IsRunning(sessionId);
}
