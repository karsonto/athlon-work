namespace Athlon.Agent.Skills.Repository;

/// <summary>
/// Loads skills from <c>baseDir/&lt;skill-name&gt;/SKILL.md</c> plus optional resource files.
/// </summary>
public sealed class FileSystemSkillRepository : IAgentSkillRepository
{
    private readonly string _baseDir;
    private readonly Action<string, Exception>? _onSkillLoadFailed;

    public FileSystemSkillRepository(string baseDir, Action<string, Exception>? onSkillLoadFailed = null)
    {
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            throw new ArgumentException("Base directory cannot be null or empty", nameof(baseDir));
        }

        _baseDir = Path.GetFullPath(baseDir);
        _onSkillLoadFailed = onSkillLoadFailed;
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

    public IReadOnlyList<AgentSkill> GetAllSkills() =>
        SkillFileSystemHelper.GetAllSkills(_baseDir, _onSkillLoadFailed);

    public bool SkillExists(string skillName) => SkillFileSystemHelper.SkillExists(_baseDir, skillName);

    public AgentSkillRepositoryInfo GetRepositoryInfo() =>
        new("filesystem", _baseDir, writeable: false);
}
