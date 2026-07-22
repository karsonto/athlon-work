using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class SessionPlanPromptContributor(
    ISessionHarnessState harnessState,
    ISessionPlanStore planStore,
    IAgentRunContextAccessor runContextAccessor) : IRuntimeContextContributor
{
    public int Priority => 34;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        var sessionId = runContextAccessor.Current?.SessionId ?? context.Session.Id;
        var plan = planStore.GetAsync(sessionId).GetAwaiter().GetResult();
        if (!plan.HasContent)
        {
            return;
        }

        var status = SessionPlanStatuses.Normalize(plan.Status);
        var isPlanMode = harnessState.IsPlanModeForActiveRun(runContextAccessor)
            || PromptModeHelper.IsPlanMode(context);
        var isCoding = harnessState.IsCodingModeForActiveRun(runContextAccessor)
            || PromptModeHelper.IsCodingMode(context);

        if (isPlanMode)
        {
            if (status is not (SessionPlanStatuses.Draft or SessionPlanStatuses.AwaitingConfirmation or SessionPlanStatuses.Approved))
            {
                return;
            }

            AppendPlan(builder, plan, "Current Session Plan (draft / awaiting confirmation)");
            builder.AppendLine("Update with update_plan if the user revises; then wait for UI confirmation.");
            builder.AppendLine();
            return;
        }

        if (isCoding && string.Equals(status, SessionPlanStatuses.Approved, StringComparison.OrdinalIgnoreCase))
        {
            AppendPlan(builder, plan, "Approved Session Plan — follow this while implementing");
            builder.AppendLine("Do not silently deviate; update todos or ask the user to return to Plan mode for spec changes.");
            builder.AppendLine();
        }
    }

    private static void AppendPlan(StringBuilder builder, SessionPlan plan, string heading)
    {
        builder.AppendLine();
        builder.Append("## ").AppendLine(heading);
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(plan.Title))
        {
            builder.Append("Title: ").AppendLine(plan.Title);
        }

        builder.Append("Status: ").AppendLine(plan.Status);
        if (!string.IsNullOrWhiteSpace(plan.Overview))
        {
            builder.Append("Overview: ").AppendLine(plan.Overview);
        }

        if (plan.Todos.Count > 0)
        {
            builder.AppendLine("Plan todos:");
            foreach (var todo in plan.Todos)
            {
                builder.Append("- ").Append(todo.Id).Append(": ").AppendLine(todo.Content);
            }
        }

        if (!string.IsNullOrWhiteSpace(plan.Body))
        {
            builder.AppendLine();
            builder.AppendLine("Plan body:");
            builder.AppendLine(plan.Body);
        }

        builder.AppendLine();
    }
}
