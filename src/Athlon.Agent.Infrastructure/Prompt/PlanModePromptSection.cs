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
        builder.AppendLine("- Follow the active plan phase instructions injected in runtime context.");
        builder.AppendLine("- Explore with read/search tools only; never edit project files or run shell in Plan mode.");
        builder.AppendLine("- You own a multi-turn consulting loop: ask with ask_plan_clarification when ambiguous, or reply with a short follow-up question in plain text when a card is unnecessary.");
        builder.AppendLine("- When information is sufficient, call publish_plan yourself (title, overview, ## Steps, ## Acceptance). Nothing auto-advances to drafting.");
        builder.AppendLine("- Prefer mermaid flowcharts for multi-step architecture when helpful.");
        builder.AppendLine("- After publishing, stop — the user will Build (switch to Coding) or send a revision.");
        builder.AppendLine("- Do not start implementing, applying patches, or claiming the work is done.");
        builder.AppendLine();
    }
}
