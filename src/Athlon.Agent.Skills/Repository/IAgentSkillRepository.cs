namespace Athlon.Agent.Skills.Repository;

public sealed class AgentSkillRepositoryInfo(string type, string location, bool writeable)
{
    public string Type { get; } = type;
    public string Location { get; } = location;
    public bool Writeable { get; } = writeable;
}

public interface IAgentSkillRepository
{
    AgentSkill? GetSkill(string name);

    IReadOnlyList<string> GetAllSkillNames();

    IReadOnlyList<AgentSkill> GetAllSkills();

    bool SkillExists(string skillName);

    AgentSkillRepositoryInfo GetRepositoryInfo();

    string Source { get; }
}
