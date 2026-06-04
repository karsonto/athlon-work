using System.Text;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Core.Prompt;

public sealed class SubAgentPersonaSection : IEnvironmentPromptSection
{
    public int Order => 50;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        var role = AmbientSubAgentRoleScope.CurrentRole;
        if (string.IsNullOrWhiteSpace(role))
        {
            return;
        }

        builder.AppendLine(role.Trim());
        builder.AppendLine();

        builder.AppendLine("You are invoked by the parent agent via call_assistant.");
        builder.AppendLine("Do not call call_assistant or delegate to other agents.");
        builder.AppendLine("Use file tools, MCP tools, and load_skill_through_path when needed.");
        builder.AppendLine("Deliver a clear, self-contained result the parent agent can synthesize.");
        builder.AppendLine();
    }
}
