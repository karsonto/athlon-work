namespace Athlon.Agent.Core;

public enum AgentRunKind
{
    Root,
    SubAgent,
    /// <summary>Leader or Worker turn inside an Agent Team task.</summary>
    TeamMember
}
