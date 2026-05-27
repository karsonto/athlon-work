namespace Athlon.Agent.Core.Compaction;

public enum CompactionKind
{
    [Obsolete("Replaced by ConversationCompact.")]
    Microcompact,

    [Obsolete("Replaced by ConversationCompact.")]
    AutoCompact,

    ConversationCompact,
    ManualCompact
}
