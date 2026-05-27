namespace Athlon.Agent.Core.Compaction;

public interface IPreCompletionPipeline
{
    Task<AgentSession> RunAsync(AgentSession session, CancellationToken cancellationToken = default);
}
