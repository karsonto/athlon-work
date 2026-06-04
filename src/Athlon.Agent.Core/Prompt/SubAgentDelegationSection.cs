using System.Text;

namespace Athlon.Agent.Core.Prompt;

/// <summary>Guidance for the parent agent on when and how to delegate via call_assistant.</summary>
public sealed class SubAgentDelegationSection(AppSettings settings) : IEnvironmentPromptSection
{
    public int Order => 550;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!settings.SubAgent.Enabled)
        {
            return;
        }

        var toolName = context.Tools.FirstOrDefault(tool =>
            string.Equals(tool.Name, "call_assistant", StringComparison.OrdinalIgnoreCase))?.Name
            ?? "call_assistant";

        builder.AppendLine("## Delegating sub-tasks");
        builder.AppendLine($"Use `{toolName}` when a focused sub-run with tools and memory helps (research, multi-step file work, isolated experiments).");
        builder.AppendLine("- **New session:** provide `role` (who the child is, boundaries, output style) and `message` (this turn's task, paths, acceptance criteria).");
        builder.AppendLine("- **Continue:** pass `session_id` from the prior tool result and a new `message`; `role` is optional (updates the child's role if provided).");
        builder.AppendLine("- You may name a skill in `message` or let the child use `load_skill_through_path` from the skills list.");
        builder.AppendLine("- Wait for the tool result; summarize for the user. The child cannot spawn nested agents.");
        builder.AppendLine();
    }
}
