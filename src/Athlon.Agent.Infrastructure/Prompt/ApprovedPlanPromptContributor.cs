using System.Text;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

/// <summary>Injects the approved Session Plan into Coding mode context.</summary>
public sealed class ApprovedPlanPromptContributor(
    ISessionHarnessState harnessState,
    IPlanRunStore planRunStore) : IRuntimeContextContributor
{
    public int Priority => 15;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (harnessState.GetMode(context.Session.Id) != SessionAgentMode.Coding)
        {
            return;
        }

        var run = planRunStore.LoadApprovedAsync(context.Session.Id).GetAwaiter().GetResult();
        if (run is null)
        {
            return;
        }

        var content = !string.IsNullOrWhiteSpace(run.PlanMarkdown)
            ? run.PlanMarkdown
            : planRunStore.ReadPlanMarkdownAsync(context.Session.Id).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        builder.AppendLine("Approved Session Plan (source of truth — implement this; update todos if scope changes):");
        builder.AppendLine("<approved_session_plan>");
        builder.AppendLine(content.Trim());
        builder.AppendLine("</approved_session_plan>");
        builder.AppendLine();
    }
}
