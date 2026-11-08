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

    /// <summary>
    /// Obsolete: ignored at runtime. Leader may redispatch freely within the global MaxRepairRounds safety valve.
    /// Kept so older profile JSON still deserializes.
    /// </summary>
    [Obsolete("Ignored at runtime. Redispatch is gated by TeamsSettings.MaxRepairRounds only.")]
    public bool AllowRedispatch { get; set; }

    /// <summary>
    /// Obsolete: ignored at runtime. Per-worker iteration caps were removed; attempt count is trace-only.
    /// Kept so older profile JSON still deserializes.
    /// </summary>
    [Obsolete("Ignored at runtime. Use TeamsSettings.MaxRepairRounds as the silent global safety valve.")]
    public int MaxIterations { get; set; } = 1;

    public bool InheritSessionKnowledge { get; set; }
}
