namespace Athlon.Agent.Core.Compaction;

public enum CompactionStrategy
{
    ConversationCompact,
    ForceCompact,
    ManualCompact,
    MiddleCutOnRetrySkipped,
}

public enum CompactionLayer
{
    TruncateArgs,
    ConversationCompact,
    ToolResultEviction,
}
