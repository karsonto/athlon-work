namespace Athlon.Agent.Core.Compaction;

public sealed record ConversationCompactResult(AgentSession Session, bool Compacted);

public interface IConversationCompactor
{
    Task<ConversationCompactResult> CompactIfNeededAsync(
        AgentSession session,
        CompactionKind kind,
        bool force,
        bool emitAudit,
        CancellationToken cancellationToken = default);
}
