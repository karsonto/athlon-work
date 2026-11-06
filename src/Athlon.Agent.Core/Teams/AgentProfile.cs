namespace Athlon.Agent.Core.Teams;

public sealed class AgentProfile
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Role / soul guidance (SOUL.md equivalent).</summary>
    public string SoulMd { get; set; } = "";

    /// <summary>Operating instructions (AGENT.md equivalent).</summary>
    public string AgentMd { get; set; } = "";

    public string? ModelNameOverride { get; set; }

    /// <summary>Empty = default safe tool set for Workers / Leader orchestration set for Leaders.</summary>
    public List<string> ToolAllowList { get; set; } = new();

    public List<string> SkillNames { get; set; } = new();

    public List<string> McpServerNames { get; set; } = new();

    public List<string> KnowledgeModuleIds { get; set; } = new();

    public int? MaxModelToolRounds { get; set; }

    /// <summary>Workers only: whether Leader may redispatch this profile.</summary>
    public bool AllowRedispatch { get; set; }

    /// <summary>Hard cap for redispatches (inclusive of first run). Default 1 = no loop.</summary>
    public int MaxIterations { get; set; } = 1;

    public bool InheritSessionKnowledge { get; set; }
}
