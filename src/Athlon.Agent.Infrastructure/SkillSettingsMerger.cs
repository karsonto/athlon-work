using Athlon.Agent.Core;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Infrastructure;

public static class SkillSettingsMerger
{
    public static List<SkillSettings> Merge(
        string skillsRootPath,
        IReadOnlyList<AgentSkill> installedSkills,
        IReadOnlyList<SkillSettings> saved)
    {
        var folderByName = SkillFileSystemHelper.GetSkillNameToFolderMap(skillsRootPath);
        var savedByName = saved
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Name))
            .GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var merged = new List<SkillSettings>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in installedSkills.OrderBy(skill => skill.Name, StringComparer.Ordinal))
        {
            savedByName.TryGetValue(skill.Name, out var existing);
            var folderName = folderByName.TryGetValue(skill.Name, out var folder)
                ? folder
                : skill.Name;

            merged.Add(new SkillSettings
            {
                Name = skill.Name,
                Enabled = existing?.Enabled ?? true,
                Path = string.IsNullOrWhiteSpace(existing?.Path) ? folderName : existing!.Path
            });
            seen.Add(skill.Name);
        }

        foreach (var orphan in saved.Where(skill => !string.IsNullOrWhiteSpace(skill.Name))
                     .OrderBy(skill => skill.Name, StringComparer.Ordinal))
        {
            if (seen.Contains(orphan.Name))
            {
                continue;
            }

            merged.Add(new SkillSettings
            {
                Name = orphan.Name,
                Enabled = orphan.Enabled,
                Path = orphan.Path
            });
        }

        return merged;
    }
}
