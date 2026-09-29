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
        EmitCompactionAudit = true,
        Strategy = CompactionStrategy.ForceCompact
    };

    public static PreCompletionOptions ManualCompact { get; } = new()
    {
        AllowTruncateArgs = true,
        AllowConversationCompact = true,
        ForceConversationCompact = true,
        EmitCompactionAudit = true,
        Strategy = CompactionStrategy.ManualCompact
    };

    /// <summary>
    /// Model-requested compact. Forces a summary even when utilization is still below the
    /// automatic threshold. Overflow retry does not set <see cref="ApplyEvenWhenBelowThreshold"/>.
    /// </summary>
    public static PreCompletionOptions ModelRequested { get; } = new()
    {
        AllowTruncateArgs = true,
        AllowConversationCompact = true,
        ForceConversationCompact = true,
        EmitCompactionAudit = true,
        Strategy = CompactionStrategy.ConversationCompact,
        ApplyEvenWhenBelowThreshold = true
    };

    public bool AllowTruncateArgs { get; init; } = true;

    public bool AllowConversationCompact { get; init; } = true;

    public bool ForceConversationCompact { get; init; }

    /// <summary>
    /// When true, a forced compact still runs after prune even if utilization fell below the target.
    /// </summary>
    public bool ApplyEvenWhenBelowThreshold { get; init; }

    public bool EmitCompactionAudit { get; init; } = true;

    public CompactionKind CompactionKind { get; init; } = CompactionKind.ConversationCompact;

    public CompactionStrategy Strategy { get; init; } = CompactionStrategy.ConversationCompact;
}
