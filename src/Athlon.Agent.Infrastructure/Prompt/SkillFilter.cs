using Athlon.Agent.Core;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Infrastructure.Prompt;

public static class SkillFilter
{
    public static IReadOnlyList<AgentSkill> GetEnabledSkills(IAgentSkillCatalog catalog, AppSettings settings)
    {
        var disabled = settings.Skills
            .Where(skill => !skill.Enabled && !string.IsNullOrWhiteSpace(skill.Name))
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return catalog.Skills
            .Where(skill => !disabled.Contains(skill.Name))
            .OrderBy(skill => skill.Name, StringComparer.Ordinal)
            .ToArray();
    }
}
