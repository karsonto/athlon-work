namespace Athlon.Agent.Skills.Repository;

/// <summary>
/// Loads skills from <c>baseDir/&lt;skill-name&gt;/SKILL.md</c> plus optional resource files.
/// </summary>
public sealed class FileSystemSkillRepository : IAgentSkillRepository
{
    private readonly string _baseDir;
    private readonly string _source;

    public FileSystemSkillRepository(string baseDir, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            throw new ArgumentException("Base directory cannot be null or empty", nameof(baseDir));
        }

        _baseDir = Path.GetFullPath(baseDir);
        Directory.CreateDirectory(_baseDir);
        _source = source ?? BuildDefaultSource();
    }

    public string Source => _source;

    public AgentSkill? GetSkill(string name)
    {
        if (!SkillExists(name))
        {
            return null;
        }

        return SkillFileSystemHelper.LoadSkill(_baseDir, name, _source);
    }

    public IReadOnlyList<string> GetAllSkillNames() => SkillFileSystemHelper.GetAllSkillNames(_baseDir);

    public IReadOnlyList<AgentSkill> GetAllSkills() => SkillFileSystemHelper.GetAllSkills(_baseDir, _source);

    public bool SkillExists(string skillName) => SkillFileSystemHelper.SkillExists(_baseDir, skillName);

    public AgentSkillRepositoryInfo GetRepositoryInfo() =>
        new("filesystem", _baseDir, writeable: false);

    private string BuildDefaultSource()
    {
        var fileName = Path.GetFileName(_baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parent = Directory.GetParent(_baseDir)?.Name;
        return parent is null or "" ? fileName : $"{parent}_{fileName}";
    }
}
