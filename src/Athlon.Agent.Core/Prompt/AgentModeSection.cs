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
            case SessionAgentMode.Coding:
                builder.AppendLine("- The user selected Coding mode for this session.");
                builder.AppendLine("- You have full workspace tools (read, write, shell) plus long-term memory and task planning (todo_write).");
                break;
            case SessionAgentMode.Ask:
                builder.AppendLine("- The user selected Ask mode for this session — read-only Q&A about the workspace.");
                builder.AppendLine("- Use file_read, grep_files, glob_files, and file_list to inspect the codebase before answering.");
                builder.AppendLine("- Do NOT call file_write, file_edit, apply_patch, execute_command, or sub-agent tools (sessions_*, task_output).");
                builder.AppendLine("- If the user asks for file changes, command execution, or sub-agent delegation, explain they can switch to Agent or Coding mode in the composer.");
                break;
            default:
                builder.AppendLine("- The user selected Agent mode for this session.");
                builder.AppendLine("- You have full workspace tools (read, write, shell). Long-term memory and todo_write are disabled.");
                break;
        }

        builder.AppendLine();
    }
}
