namespace Athlon.Agent.Core.SubAgents;

public sealed class SubAgentSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxToolRounds { get; set; } = 16;
    public int MaxNestingDepth { get; set; } = 2;
    public int MaxConcurrentSubAgents { get; set; } = 4;
    public int DefaultSyncTimeoutSeconds { get; set; } = 30;
    public int MaxSyncTimeoutSeconds { get; set; } = 600;
    public int MaxPendingCompletionsPerParent { get; set; } = 20;
}
