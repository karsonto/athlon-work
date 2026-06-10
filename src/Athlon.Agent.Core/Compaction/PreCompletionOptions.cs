namespace Athlon.Agent.Core.Compaction;

public sealed class PreCompletionOptions
{
    public static PreCompletionOptions Default { get; } = new();

    public static PreCompletionOptions AgentLoop { get; } = new()
    {
        AllowTruncateArgs = true,
        AllowConversationCompact = true,
        EmitCompactionAudit = true
    };

    public static PreCompletionOptions ForceCompact { get; } = new()
    {
        AllowTruncateArgs = true,
        AllowConversationCompact = true,
        ForceConversationCompact = true,
        EmitCompactionAudit = true
    };

    public bool AllowTruncateArgs { get; init; } = true;

    public bool AllowConversationCompact { get; init; } = true;

    public bool ForceConversationCompact { get; init; }

    public bool EmitCompactionAudit { get; init; } = true;

    public CompactionKind CompactionKind { get; init; } = CompactionKind.ConversationCompact;
}
