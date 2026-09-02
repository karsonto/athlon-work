using System.Text;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class PlanRuntimeContextContributor(
    ISessionHarnessState harnessState,
    IPlanPhaseAccessor phaseAccessor) : IRuntimeContextContributor
{
    public int Priority => 5;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (harnessState.GetMode(context.Session.Id) != SessionAgentMode.Plan)
        {
            return;
        }

        var run = phaseAccessor.GetActiveRun(context.Session.Id);
        if (run is null)
        {
            builder.AppendLine("Plan mode is active but no plan run is loaded yet.");
            return;
        }

        builder.AppendLine("Active plan run:");
        builder.AppendLine($"- run_id: {run.Id}");
        builder.AppendLine($"- phase: {run.Phase}");
        builder.AppendLine($"- status: {run.Status}");
        if (!string.IsNullOrWhiteSpace(run.PlanPath))
        {
            builder.AppendLine($"- plan_path: {run.PlanPath}");
        }

        if (!string.IsNullOrWhiteSpace(run.Goal))
        {
            builder.AppendLine($"- goal: {run.Goal}");
        }

        if (!string.IsNullOrWhiteSpace(run.Title))
        {
            builder.AppendLine($"- title: {run.Title}");
        }

        builder.AppendLine();
        builder.AppendLine(PlanPhaseInstructions.For(run.Phase));
        builder.AppendLine();
    }
}

internal static class PlanPhaseInstructions
{
    internal static string For(PlanPhase phase) => phase switch
    {
        PlanPhase.Explore =>
            "Phase Explore: read/search the workspace to understand scope. Do not call publish_plan yet. "
            + "If the goal or approach is unclear, call ask_plan_clarification with 1–3 questions and concrete options, then stop. "
            + "Do not edit files or run shell. If information is sufficient, summarize briefly; the next phase will draft the plan.",
        PlanPhase.AwaitClarify =>
            "Phase AwaitClarify: you already asked the user. Do not call publish_plan or ask_plan_clarification again. "
            + "Wait for the user's selection or notes.",
        PlanPhase.Draft =>
            "Phase Draft: call publish_plan with title, overview, and a markdown body that includes "
            + "`## Steps` (numbered) and `## Acceptance` (checklist). Optional todos array seeds Coding tasks after Build. "
            + "Do not implement code.",
        PlanPhase.AwaitConfirm =>
            "Phase AwaitConfirm: the plan is waiting for the user. If they send new instructions, treat them as a revision request. "
            + "Do not call publish_plan, edit files, or start implementation unless this turn is a revision Draft.",
        PlanPhase.Done =>
            "Phase Done: this plan run is finished. Do not continue planning.",
        _ => "Follow Plan mode rules."
    };
}
