namespace Athlon.Agent.Core.Compaction;

public sealed record CompactionExecutionRequest(
    CompactionKind Kind,
    bool Force,
    bool EmitAudit,
    CompactionStrategy Strategy = CompactionStrategy.ConversationCompact,
    CompactionRuntimeContext? RuntimeContext = null,
    DynamicCompactionPlan? Plan = null);
