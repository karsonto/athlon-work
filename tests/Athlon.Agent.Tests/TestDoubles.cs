using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

internal sealed class NoOpActiveAgentSessionContext : IActiveAgentSessionContext
{
    private static readonly AsyncLocal<string?> AmbientSessionId = new();

    public string? SessionId => AmbientSessionId.Value;

    public void SetSession(string? sessionId) => AmbientSessionId.Value = sessionId;

    public IDisposable Enter(string sessionId)
    {
        var previous = AmbientSessionId.Value;
        AmbientSessionId.Value = sessionId;
        return new SessionScope(previous);
    }

    private sealed class SessionScope(string? previous) : IDisposable
    {
        public void Dispose() => AmbientSessionId.Value = previous;
    }
}
