using Athlon.Agent.Skills;
using Athlon.Agent.Skills.Repository;

namespace Athlon.Agent.Infrastructure.SkillHub;

/// <summary>
/// Loads skills from the local skills directory. A skill enters the catalog only when its files
/// match the manifest outside the skill directory. Manifests larger than
/// <see cref="UncheckedManifestBytes"/> are admitted without that check.
/// </summary>
public sealed class IntegritySkillRepository : IAgentSkillRepository
{
    /// <summary>Manifest files above this size skip content verification on catalog load.</summary>
    internal const long UncheckedManifestBytes = 128 * 1024;
    private readonly string _skillsPath;
    private readonly string _manifestDirectory;
    private readonly string _anchorsPath;
    private readonly Action<string, Exception>? _onSkillLoadFailed;

    public IntegritySkillRepository(
        string skillsPath,
        string manifestDirectory,
        string anchorsPath,
        Action<string, Exception>? onSkillLoadFailed = null)
    {
        _skillsPath = skillsPath;
        _manifestDirectory = manifestDirectory;
        _anchorsPath = anchorsPath;
        _onSkillLoadFailed = onSkillLoadFailed;
    }

    public AgentSkill? GetSkill(string name)
    {
        foreach (var skill in GetAllSkills())
        {
            if (string.Equals(skill.Name, name, StringComparison.Ordinal))
            {
                return skill;
            }
        }

        return null;
    }

    public IReadOnlyList<string> GetAllSkillNames() =>
        GetAllSkills().Select(static skill => skill.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<AgentSkill> GetAllSkills()
    {
        var byName = new Dictionary<string, AgentSkill>(StringComparer.Ordinal);
        LoadRoot(_skillsPath, byName);
        return byName.Values.ToArray();
    }

    public bool SkillExists(string skillName) => GetSkill(skillName) is not null;

    public AgentSkillRepositoryInfo GetRepositoryInfo() =>
        new("filesystem", _skillsPath, writeable: false);

    private void LoadRoot(string root, Dictionary<string, AgentSkill> byName)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!SkillFileSystemHelper.HasSkillFile(dir))
            {
                continue;
            }

            var name = SkillFileSystemHelper.TryReadSkillName(dir);
            if (string.IsNullOrWhiteSpace(name) || byName.ContainsKey(name))
            {
                continue;
            }

            try
            {
                var manifestPath = SkillAnchorStore.ManifestPath(_manifestDirectory, name);
                if (!File.Exists(manifestPath))
                {
                    throw new InvalidOperationException("skill manifest is missing");
                }

                if (new FileInfo(manifestPath).Length <= UncheckedManifestBytes)
                {
                    var manifestText = SkillIntegrity.ReadUtf8(manifestPath).TrimEnd('\r', '\n');
                    var anchor = SkillAnchorStore.Get(_anchorsPath, name);
                    if (!SkillIntegrity.TryVerify(dir, manifestText, anchor, name, out var error))
                    {
                        throw new InvalidOperationException(error);
                    }
                }

                byName[name] = SkillFileSystemHelper.LoadSkillFromDirectory(dir);
            }
            catch (Exception ex)
            {
                _onSkillLoadFailed?.Invoke(dir, ex);
            }
        }
    }
}
