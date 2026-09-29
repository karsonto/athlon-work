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
        builder.AppendLine("- You are producing an implementation plan for the user to review before any coding.");
        builder.AppendLine("- Read and search only. Do not edit project files, run shell, or start implementing.");
        builder.AppendLine("- You own a multi-turn consulting loop: ask with ask_user when ambiguous, then stop.");
        builder.AppendLine("- When information is sufficient, call publish_plan yourself. Nothing auto-advances to drafting.");
        builder.AppendLine("- After publishing, stop. The user will Build or send a revision.");
        builder.AppendLine("- Follow the active plan phase instructions in runtime context.");
        builder.AppendLine();
    }
}
