namespace Athlon.Agent.Core.SubAgents;

public sealed class SubAgentSettings
{
    public bool Enabled { get; set; } = true;
    public string ToolName { get; set; } = "call_assistant";
    public string Description { get; set; } =
        "Delegate a sub-task to a child assistant (role + message; session_id to continue). "
        + "Prefer sessions_spawn / sessions_send for structured sub-agent orchestration.";
    public int MaxToolRounds { get; set; } = 16;
    public int MaxNestingDepth { get; set; } = 2;
    public int MaxConcurrentSubAgents { get; set; } = 2;
    public int DefaultSyncTimeoutSeconds { get; set; } = 30;
    public int MaxSyncTimeoutSeconds { get; set; } = 600;
    public int MaxPendingCompletionsPerParent { get; set; } = 20;
    public bool KeepCallAssistantAlias { get; set; } = true;
}
