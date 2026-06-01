namespace Athlon.Agent.Skills;

public interface IAgentSkillCatalog
{
    IReadOnlyList<AgentSkill> Skills { get; }

    AgentSkill? GetSkill(string name);

    AgentSkill? GetSkillById(string skillId);

    void Reload();
}

public sealed class AgentSkillCatalog : IAgentSkillCatalog
{
    private readonly Repository.IAgentSkillRepository _repository;
    private IReadOnlyList<AgentSkill> _skills = Array.Empty<AgentSkill>();

    public AgentSkillCatalog(Repository.IAgentSkillRepository repository)
    {
        _repository = repository;
        Reload();
    }

    public IReadOnlyList<AgentSkill> Skills => _skills;

    public AgentSkill? GetSkill(string name) =>
        _skills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.Ordinal));

    public AgentSkill? GetSkillById(string skillId) => GetSkill(skillId);

    public void Reload() => _skills = _repository.GetAllSkills();
}
