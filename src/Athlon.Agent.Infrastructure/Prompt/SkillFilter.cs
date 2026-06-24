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

    public static bool IsEnabled(AgentSkill skill, AppSettings settings) =>
        !IsDisabled(skill.Name, settings);

    public static bool IsDisabled(string skillName, AppSettings settings) =>
        settings.Skills.Any(skill =>
            !skill.Enabled
            && !string.IsNullOrWhiteSpace(skill.Name)
            && string.Equals(skill.Name, skillName, StringComparison.OrdinalIgnoreCase));
}
