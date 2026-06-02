using System.Text;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Injects active plan state into compaction summaries so long-running work survives context compression.
/// </summary>
public static class CompactionPlanContextBuilder
{
    public static string? BuildSummaryPromptAppendix(AgentPlan? plan)
    {
        if (plan is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("<active_plan_snapshot>");
        builder.AppendLine(
            "The following plan is still active. Your extracted summary MUST preserve session intent, " +
            "artifacts, and next steps aligned with this plan. Do not mark subtasks done unless already finished.");
        builder.AppendLine();
        builder.AppendLine(PlanMarkdownFormatter.ToMarkdown(plan, detailed: true));
        AppendIncompleteSubtasksSection(builder, plan);
        builder.AppendLine("</active_plan_snapshot>");
        return builder.ToString().TrimEnd();
    }

    public static string EnrichSummaryText(string summary, AgentPlan? plan)
    {
        var appendix = BuildPersistedAppendix(plan);
        if (string.IsNullOrWhiteSpace(appendix))
        {
            return summary;
        }

        return summary.TrimEnd() + "\n\n---\n\n" + appendix;
    }

    private static string? BuildPersistedAppendix(AgentPlan? plan)
    {
        if (plan is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("[Active plan snapshot — continue from this plan after compression]");
        builder.AppendLine();
        builder.AppendLine(PlanMarkdownFormatter.ToMarkdown(plan, detailed: true));
        AppendIncompleteSubtasksSection(builder, plan);
        return builder.ToString().TrimEnd();
    }

    private static void AppendIncompleteSubtasksSection(StringBuilder builder, AgentPlan plan)
    {
        var incomplete = plan.Subtasks
            .Where(subtask => subtask.State is SubTaskState.Todo or SubTaskState.InProgress)
            .ToList();
        if (incomplete.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("## Incomplete subtasks (must remain open in summary)");
        foreach (var subtask in incomplete)
        {
            var marker = subtask.State == SubTaskState.InProgress ? "[IN PROGRESS]" : "[TODO]";
            builder.AppendLine($"- {marker} **{subtask.Name}**: {subtask.Description}");
            builder.AppendLine($"  - Expected outcome: {subtask.ExpectedOutcome}");
        }
    }
}
