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
            case SessionAgentMode.Plan:
                builder.AppendLine("- The user selected Session Plan mode for this session.");
                builder.AppendLine("- Read-only exploration only.");
                if (PromptModeHelper.HasAny(context, "file_read", "grep_files", "glob_files", "file_list"))
                {
                    builder.AppendLine("- Prefer file_read, grep_files, glob_files, and file_list when advertised.");
                }

                if (PromptModeHelper.HasAny(context, "memory_search", "memory_get"))
                {
                    builder.AppendLine("- Use memory_* tools when advertised for prior session knowledge.");
                }

                if (PromptModeHelper.HasKnowledgeTool(context))
                {
                    builder.AppendLine("- Use knowledge_* when advertised for uploaded documents.");
                }

                if (PromptModeHelper.HasAny(context, "create_plan", "update_plan"))
                {
                    builder.AppendLine("- Produce a detailed plan via create_plan / update_plan (mermaid flowcharts for multi-step work).");
                }

                builder.AppendLine("- After publishing or updating the plan, stop and wait for the user to confirm or revise — do not edit code or run shell.");
                break;
            case SessionAgentMode.Coding:
                builder.AppendLine("- The user selected Coding mode for this session.");
                builder.AppendLine("- You have full workspace tools (read, write, shell) as advertised, plus long-term memory and task planning when those tools are present.");
                if (PromptModeHelper.HasTool(context, "todo_write"))
                {
                    builder.AppendLine("- For multi-step or multi-file work: maintain todos with todo_write; if an approved Session Plan is injected, follow it.");
                }

                builder.AppendLine("- Direct Coding without a prior Plan is allowed — explore, write todos, implement, and verify.");
                break;
            case SessionAgentMode.Ask:
                builder.AppendLine("- The user selected Ask mode for this session — read-only Q&A about the workspace.");
                builder.AppendLine("- Follow the tool decision tree below; unavailable mutating tools are not permitted.");
                break;
            case SessionAgentMode.Debug:
                builder.AppendLine("- The user selected Debug mode — investigate a reproducible bug with runtime logs.");
                builder.AppendLine("- Evidence gate: do not state a root cause and do not apply a fix until you have called debug_read_logs and cited matching log hits.");
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
