using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;
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
        var allowed = AgentProfileTurnScope.SkillNames ?? ScheduleTurnScope.Current?.SkillNames;
        if (allowed is not null)
        {
            // Empty allow-list: deny (matches profile UI "skills×0" and MCP empty semantics).
            if (allowed.Count == 0)
            {
                return false;
            }

            // Team Worker / schedule assignment is an explicit allow for this turn — do not let a
            // global sidebar disable block load_skill_through_path for skills the profile selected.
            return allowed.Any(name =>
                string.Equals(name, skill.Name, StringComparison.OrdinalIgnoreCase));
        }

        return !IsDisabled(skill.Name, settings);
    }

    public static bool IsDisabled(string skillName, AppSettings settings) =>
        settings.Skills.Any(skill =>
            !skill.Enabled
            && !string.IsNullOrWhiteSpace(skill.Name)
            && string.Equals(skill.Name, skillName, StringComparison.OrdinalIgnoreCase));
}
