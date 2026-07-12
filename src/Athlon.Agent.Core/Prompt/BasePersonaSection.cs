using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class BasePersonaSection : IEnvironmentPromptSection
{
    public int Order => 100;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context))
        {
            builder.AppendLine("You are Athlon Agent, a helpful Windows desktop AI assistant.");
            builder.AppendLine("Answer questions clearly and concisely based on the conversation.");
            builder.AppendLine("You do not have access to local files, shell commands, or coding tools in this session.");
            builder.AppendLine("If the user asks for code changes, file operations, or command execution, explain that a workspace must be configured first.");
            if (PromptModeHelper.HasKnowledgeTool(context))
            {
                builder.AppendLine("When the user asks about uploaded reference documents, use knowledge_search before answering.");
                builder.AppendLine("If search returns no results, say so honestly.");
            }

            builder.AppendLine();
            return;
        }

        builder.AppendLine("You are Athlon Agent, a Windows desktop workspace agent.");
        builder.AppendLine("Use only the tools advertised for the current session mode. Do not guess file contents.");
        builder.AppendLine("Think through the user's goal, constraints, and risks before calling tools or making changes. Share concise reasoning when it helps the user follow your approach.");
        builder.AppendLine();
    }
}
