using System.Text;
using Athlon.Agent.Core;
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
        if (run.Phase == PlanPhase.Explore && HasClarificationAnswers(context.Session))
        {
            builder.AppendLine(
                "The user just submitted clarification answers in this turn. Do not idle or say you are waiting. "
                + "If answers are still insufficient, call ask_user again; otherwise explore the workspace or call publish_plan.");
        }

        builder.AppendLine();
    }

    private static bool HasClarificationAnswers(AgentSession session)
    {
        for (var i = session.Messages.Count - 1; i >= 0; i--)
        {
            var message = session.Messages[i];
            if (message.Role != MessageRole.User)
            {
                continue;
            }

            return message.Content.StartsWith("Clarification answers:", StringComparison.Ordinal);
        }

        return false;
    }
}

internal static class PlanPhaseInstructions
{
    internal static string For(PlanPhase phase) => phase switch
    {
        PlanPhase.Explore =>
            "Phase Explore (consulting): You control the loop. Read/search the workspace as needed. "
            + "When the goal, stack, scope, or approach is still ambiguous, call ask_user (1–3 questions with concrete options) and stop. "
            + "You may ask across multiple user turns. When you have enough information, call publish_plan yourself — nothing auto-drafts for you. "
            + "Do not edit files or run shell. Do not pretend to wait without calling ask_user.",
        PlanPhase.AwaitClarify =>
            "Phase AwaitClarify: questions were already asked in the QuestionBar and this turn should have ended. "
            + "Do not call publish_plan or ask_user again, and do not generate waiting copy.",
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
