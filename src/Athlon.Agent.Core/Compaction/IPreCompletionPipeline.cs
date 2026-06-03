namespace Athlon.Agent.Core.Compaction;

public interface IPreCompletionPipeline
{
    Task<AgentSession> RunAsync(
        AgentSession session,
        PreCompletionOptions? options = null,
        CompactionRuntimeContext? runtimeContext = null,
        CancellationToken cancellationToken = default);
}
