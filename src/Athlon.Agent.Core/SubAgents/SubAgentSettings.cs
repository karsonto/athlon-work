namespace Athlon.Agent.Core.SubAgents;

public sealed class SubAgentSettings
{
    public bool Enabled { get; set; } = true;
    public string ToolName { get; set; } = "call_assistant";
    public string Description { get; set; } =
        "Delegate a sub-task to a child assistant (role + message; session_id to continue). "
        + "Child has same file tools, skills, MCP, and compaction as parent; cannot spawn nested agents.";
    public int MaxToolRounds { get; set; } = 16;
    public int MaxNestingDepth { get; set; } = 2;
    public int MaxHandoffChars { get; set; } = 4000;
}
