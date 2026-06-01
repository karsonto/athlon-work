namespace Athlon.Agent.Skills.Repository;

/// <summary>
/// Loads skills from <c>baseDir/&lt;skill-name&gt;/SKILL.md</c> plus optional resource files.
/// </summary>
public sealed class FileSystemSkillRepository : IAgentSkillRepository
{
    private readonly string _baseDir;

    public FileSystemSkillRepository(string baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            throw new ArgumentException("Base directory cannot be null or empty", nameof(baseDir));
        }

        _baseDir = Path.GetFullPath(baseDir);
        Directory.CreateDirectory(_baseDir);
    }

    public AgentSkill? GetSkill(string name)
    {
        if (!SkillExists(name))
        {
            return null;
        }

        return SkillFileSystemHelper.LoadSkill(_baseDir, name);
    }

    public IReadOnlyList<string> GetAllSkillNames() => SkillFileSystemHelper.GetAllSkillNames(_baseDir);

    public IReadOnlyList<AgentSkill> GetAllSkills() => SkillFileSystemHelper.GetAllSkills(_baseDir);

    public bool SkillExists(string skillName) => SkillFileSystemHelper.SkillExists(_baseDir, skillName);

    public AgentSkillRepositoryInfo GetRepositoryInfo() =>
        new("filesystem", _baseDir, writeable: false);
}
