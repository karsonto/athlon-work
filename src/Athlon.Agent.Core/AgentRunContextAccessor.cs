namespace Athlon.Agent.Core;

public sealed class AgentRunContextAccessor : IAgentRunContextAccessor
{
    private static readonly AsyncLocal<AgentRunContext?> Ambient = new();

    public AgentRunContext? Current => Ambient.Value;

    public IDisposable Push(AgentRunContext context) => new Scope(context);

    public string ResolveSessionDirectory(string sessionsPath, string sessionId) =>
        Current?.ResolveSessionDirectory(sessionsPath, sessionId) ?? Path.Combine(sessionsPath, sessionId);

    private sealed class Scope : IDisposable
    {
        private readonly AgentRunContext? _previous;

        public Scope(AgentRunContext context)
        {
            _previous = Ambient.Value;
            Ambient.Value = context;
        }

        public void Dispose() => Ambient.Value = _previous;
    }
}
