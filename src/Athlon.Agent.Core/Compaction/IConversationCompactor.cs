namespace Athlon.Agent.Core.Compaction;

public sealed record ConversationCompactResult(AgentSession Session, bool Compacted);

public interface IConversationCompactor
{
    Task<ConversationCompactResult> CompactIfNeededAsync(
        AgentSession session,
        CompactionExecutionRequest request,
        CancellationToken cancellationToken = default);
}
