using System.Text;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class PlanModePromptSection : IEnvironmentPromptSection
{
    public string Name => "session:plan-mode";

    public int Order => PromptSectionBands.Mode + 1;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (context.AgentMode != SessionAgentMode.Plan)
        {
            return;
        }

        builder.AppendLine("Plan mode workflow:");
        builder.AppendLine("- Read/search only until the user Builds; never implement in Plan mode.");
        builder.AppendLine("- Follow the active plan phase instructions in runtime context (ask_user / publish_plan timing).");
        builder.AppendLine();
    }
}
