namespace Athlon.Agent.Core;

public interface IAgentRunContextAccessor
{
    AgentRunContext? Current { get; }

    IDisposable Push(AgentRunContext context);

    string ResolveSessionDirectory(string sessionsPath, string sessionId);
}
