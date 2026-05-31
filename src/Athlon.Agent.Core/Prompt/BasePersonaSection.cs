using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class BasePersonaSection : IEnvironmentPromptSection
{
    public int Order => 100;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        builder.AppendLine("You are Athlon Agent, a Windows desktop coding agent.");
        builder.AppendLine("Use the provided function tools when you need to inspect or modify workspace files. Do not guess file contents.");
        builder.AppendLine("Think through the user's goal, constraints, and risks before calling tools or making changes. Share concise reasoning when it helps the user follow your approach.");
        builder.AppendLine();
    }
}
