using Athlon.Agent.Core;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Infrastructure.Prompt;

public static class SkillFilter
{
    public static IReadOnlyList<AgentSkill> GetEnabledSkills(IAgentSkillCatalog catalog, AppSettings settings)
    {
        return catalog.Skills
            .Where(skill => IsEnabled(skill, settings))
            .OrderBy(skill => skill.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool IsEnabled(AgentSkill skill, AppSettings settings)
    {
        if (IsDisabled(skill.Name, settings))
        {
            return false;
        }

        var allowed = ScheduleTurnScope.Current?.SkillNames;
        if (allowed is null)
        {
            return true;
        }

        return allowed.Any(name =>
            string.Equals(name, skill.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDisabled(string skillName, AppSettings settings) =>
        settings.Skills.Any(skill =>
            !skill.Enabled
            && !string.IsNullOrWhiteSpace(skill.Name)
            && string.Equals(skill.Name, skillName, StringComparison.OrdinalIgnoreCase));
}
