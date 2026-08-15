using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class SkillsSection(AppSettings settings, IAgentSkillCatalog catalog) : IEnvironmentPromptSection
{
    public string Name => "skills:catalog";

    public int Order => PromptSectionBands.Skills;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public PromptOccupancyKind OccupancyKind => PromptOccupancyKind.Skills;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        var skills = SkillFilter.GetEnabledSkills(catalog, settings);
        if (skills.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        SkillXmlPromptRenderer.AppendSkillPrompt(builder, skills);
    }
}
