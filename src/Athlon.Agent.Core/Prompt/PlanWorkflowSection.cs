using System.Text;

namespace Athlon.Agent.Core.Prompt;

/// <summary>Session Plan mode workflow — detailed specs with mermaid, wait for confirmation.</summary>
public sealed class PlanWorkflowSection : IEnvironmentPromptSection
{
    public int Order => 400;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!PromptModeHelper.IsPlanMode(context) || PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        builder.AppendLine("Session Plan mode workflow:");
        builder.AppendLine("- Explore with read-only tools until the goal, constraints, and touchpoints are clear; ask the user if critical requirements are missing.");
        builder.AppendLine("- Call create_plan with a DETAILED document (not a thin outline):");
        builder.AppendLine("  - title + overview (1–2 sentences)");
        builder.AppendLine("  - body as full Markdown with sections: goals/constraints, approach and non-goals, files to change, implementation steps, risks/acceptance");
        builder.AppendLine("  - for multi-step work include at least one mermaid fence (flowchart or sequenceDiagram; node IDs without spaces)");
        builder.AppendLine("  - todos[] aligned with implementation steps (stable kebab-case ids)");
        builder.AppendLine("- When the user requests changes, call update_plan with a full revised body (keep diagrams up to date); do not shrink into a bullet sketch.");
        builder.AppendLine("- After create_plan or update_plan: end the turn and wait. Do not call file_write, file_edit, apply_patch, or execute_command.");
        builder.AppendLine("- The user confirms via the UI (Confirm and start implementation), which switches to Coding — you do not switch modes yourself.");
        builder.AppendLine();
    }
}
