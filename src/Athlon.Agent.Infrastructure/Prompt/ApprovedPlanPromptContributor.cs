using System.Text;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Threading;

namespace Athlon.Agent.Infrastructure.Prompt;

/// <summary>Injects the approved Session Plan into the implementation context.</summary>
public sealed class ApprovedPlanPromptContributor(
    IPlanRunStore planRunStore) : IRuntimeContextContributor
{
    public int Priority => 15;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        var run = SyncOverAsync.Run(() => planRunStore.LoadApprovedAsync(context.Session.Id));
        if (run is null)
        {
            return;
        }

        var content = !string.IsNullOrWhiteSpace(run.PlanMarkdown)
            ? run.PlanMarkdown
            : SyncOverAsync.Run(() => planRunStore.ReadPlanMarkdownAsync(context.Session.Id));
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        builder.AppendLine("The user clicked Build and approved this plan. Start implementing it now; do not wait for another user message.");
        builder.AppendLine("Approved Session Plan (source of truth — implement this; update todos if scope changes):");
        builder.AppendLine("<approved_session_plan>");
        builder.AppendLine(content.Trim());
        builder.AppendLine("</approved_session_plan>");
        builder.AppendLine();
    }
}
