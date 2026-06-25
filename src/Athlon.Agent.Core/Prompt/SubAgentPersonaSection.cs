using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class SubAgentPersonaSection(IAgentRunContextAccessor runContextAccessor) : IEnvironmentPromptSection
{
    public int Order => 50;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        var role = runContextAccessor.Current?.SubAgentRole;
        if (string.IsNullOrWhiteSpace(role))
        {
            return;
        }

        builder.AppendLine(role.Trim());
        builder.AppendLine();

        builder.AppendLine("You are invoked by the parent agent as a sub-agent session.");
        builder.AppendLine("Do not call sessions_* tools or delegate to other agents.");
        builder.AppendLine("Use file tools, MCP tools, and load_skill_through_path when needed.");
        builder.AppendLine("End your reply with a structured result block:");
        builder.AppendLine();
        builder.AppendLine("## Result");
        builder.AppendLine("status: completed | blocked | needs_parent");
        builder.AppendLine("accomplished:");
        builder.AppendLine("- ...");
        builder.AppendLine("findings:");
        builder.AppendLine("- ...");
        builder.AppendLine("files_changed:");
        builder.AppendLine("- path/to/file");
        builder.AppendLine();
    }
}
