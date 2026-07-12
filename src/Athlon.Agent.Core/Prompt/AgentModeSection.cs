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
