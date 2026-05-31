using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class SkillsSection(AppSettings settings, IAgentSkillCatalog catalog) : IEnvironmentPromptSection
{
    public int Order => 600;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        catalog.Reload();
        var skills = SkillFilter.GetEnabledSkills(catalog, settings);
        builder.AppendLine();

        if (skills.Count == 0)
        {
            return;
        }

        SkillXmlPromptRenderer.AppendSkillPrompt(builder, skills);
    }
}
