namespace Athlon.Agent.Core.Compaction;

public enum CompactionStrategy
{
    ConversationCompact,
    ForceCompact,
    ManualCompact,
}

public enum CompactionLayer
{
    TruncateArgs,
    ConversationCompact,
    ToolResultEviction,
}
