using System.Text;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Core.Prompt;

public sealed class PlanExecutionPolicySection : IEnvironmentPromptSection
{
    public int Order => 315;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (context.InteractionMode != AgentInteractionMode.Agent
            || context.ActivePlan?.Phase != PlanPhase.Approved)
        {
            return;
        }

        builder.AppendLine("Approved plan execution:");
        builder.AppendLine("- The user approved this plan via Build. Execute it in order.");
        builder.AppendLine("- Call get_plan first when you need the full plan and subtask statuses.");
        builder.AppendLine(
            "- Work one in-progress subtask at a time; call finish_subtask with concrete measurable outcomes "
            + "(paths, counts, test results — not a narrative).");
        builder.AppendLine(
            "- Do not call create_plan during execution. To revise the plan, ask the user to switch back to Plan mode.");
        builder.AppendLine();
    }
}
