using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public sealed class AgentRunState
{
    public int ModelToolRound { get; set; }

    public ToolStormBreaker? ToolStorm { get; set; }

    public SessionUsageSnapshot? UsageSnapshot { get; set; }

    public CompactionRuntimeContext? Compaction { get; set; }

    public PendingToolApproval? PendingApproval { get; set; }
}
