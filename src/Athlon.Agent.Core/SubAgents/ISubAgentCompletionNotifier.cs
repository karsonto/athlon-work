namespace Athlon.Agent.Core.SubAgents;

public interface ISubAgentCompletionNotifier
{
    void NotifyCompletionReady(string parentSessionId);
}
