using System.Text;
using Athlon.Agent.Core.Harness;

namespace Athlon.Agent.Core.Prompt;

public sealed class AgentModeSection : IEnvironmentPromptSection
{
    public string Name => "session:mode";

    public int Order => PromptSectionBands.Mode;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        builder.AppendLine("Session mode:");
        switch (context.AgentMode)
        {
            case SessionAgentMode.Coding:
                builder.AppendLine("- The user selected Coding mode for this session.");
                builder.AppendLine("- You have full workspace tools (read, write, shell) as advertised, plus long-term memory and task planning when those tools are present.");
                if (PromptModeHelper.HasTool(context, "todo_write"))
                {
                    builder.AppendLine("- For multi-step or multi-file work: maintain todos with todo_write.");
                }

                builder.AppendLine("- Explore, write todos when useful, implement, and verify.");
                break;
            case SessionAgentMode.Ask:
                builder.AppendLine("- The user selected Ask mode for this session — read-only Q&A about the workspace.");
                builder.AppendLine("- Follow the tool decision tree below; write/patch/shell/terminal/MCP/sub-agent tools are not permitted.");
                break;
            case SessionAgentMode.Plan:
                builder.AppendLine("- The user selected Plan mode — produce an implementation plan for review before coding.");
                builder.AppendLine("- Explore with read/search tools only; if the request is ambiguous, ask with ask_plan_clarification before drafting.");
                builder.AppendLine("- Publish the plan with publish_plan in Draft; never implement.");
                builder.AppendLine("- After the plan is published, wait for the user to Build (switch to Coding) or send a revision.");
                break;
            case SessionAgentMode.Debug:
                builder.AppendLine("- The user selected Debug mode — investigate a reproducible bug with runtime logs.");
                builder.AppendLine("- Evidence gate: do not state a root cause and do not apply a fix until you have called diagnose_logs and cited matching evidence.");
                builder.AppendLine("- Empty logs or no matching entries means evidence is insufficient: adjust probes and wait for another repro. Do not guess.");
                builder.AppendLine("- Instrument, Fix, and Cleanup may edit files; Hypothesize, Analyze, and Await phases are read-only.");
                break;
            default:
                builder.AppendLine("- The user selected Agent mode for this session.");
                builder.AppendLine("- You have full workspace tools (read, write, shell) as advertised. Long-term memory and todo_write are disabled unless advertised.");
                break;
        }

        builder.AppendLine();
    }
}
