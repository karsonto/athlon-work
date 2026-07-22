using System.Text;
using Athlon.Agent.Core.Harness;

namespace Athlon.Agent.Core.Prompt;

public sealed class AgentModeSection : IEnvironmentPromptSection
{
    public int Order => 105;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        builder.AppendLine("Session mode:");
        switch (context.AgentMode)
        {
            case SessionAgentMode.Plan:
                builder.AppendLine("- The user selected Session Plan mode for this session.");
                builder.AppendLine("- Read-only exploration only: use file_read, grep_files, glob_files, file_list, memory_*, knowledge_*.");
                builder.AppendLine("- Produce a detailed plan via create_plan / update_plan (mermaid flowcharts for multi-step work).");
                builder.AppendLine("- After publishing or updating the plan, stop and wait for the user to confirm or revise — do not edit code or run shell.");
                break;
            case SessionAgentMode.Coding:
                builder.AppendLine("- The user selected Coding mode for this session.");
                builder.AppendLine("- You have full workspace tools (read, write, shell) plus long-term memory and task planning (todo_write).");
                builder.AppendLine("- For multi-step or multi-file work: maintain todos with todo_write; if an approved Session Plan is injected, follow it.");
                builder.AppendLine("- Direct Coding without a prior Plan is allowed — explore, write todos, implement, and verify.");
                break;
            case SessionAgentMode.Ask:
                builder.AppendLine("- The user selected Ask mode for this session — read-only Q&A about the workspace.");
                builder.AppendLine("- Follow the tool decision tree below; unavailable mutating tools are not permitted.");
                break;
            default:
                builder.AppendLine("- The user selected Agent mode for this session.");
                builder.AppendLine("- You have full workspace tools (read, write, shell). Long-term memory and todo_write are disabled.");
                break;
        }

        builder.AppendLine();
    }
}
