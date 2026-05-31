using System.Text;

namespace Athlon.Agent.Core.Plan;

public static class PlanMarkdownFormatter
{
    public static string ToMarkdown(AgentPlan plan, bool detailed)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Plan: {plan.Name}");
        builder.AppendLine();
        builder.AppendLine($"**Description:** {plan.Description}");
        builder.AppendLine($"**Expected outcome:** {plan.ExpectedOutcome}");
        builder.AppendLine();

        if (plan.Subtasks.Count == 0)
        {
            builder.AppendLine("_No subtasks._");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine("## Subtasks");
        builder.AppendLine();

        for (var index = 0; index < plan.Subtasks.Count; index++)
        {
            var subtask = plan.Subtasks[index];
            if (detailed)
            {
                builder.AppendLine(FormatSubtaskDetailed(index, subtask));
                builder.AppendLine();
            }
            else
            {
                builder.AppendLine(FormatSubtaskOneLine(subtask));
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatSubtaskOneLine(AgentSubTask subtask)
    {
        var prefix = subtask.State switch
        {
            SubTaskState.Todo => "- [ ]",
            SubTaskState.InProgress => "- [ ] [WIP]",
            SubTaskState.Done => "- [x]",
            SubTaskState.Abandoned => "- [ ] [Abandoned]",
            _ => "- [ ]"
        };

        return $"{prefix} {subtask.Name}";
    }

    private static string FormatSubtaskDetailed(int index, AgentSubTask subtask)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"### {index}. {FormatSubtaskOneLine(subtask)}");
        builder.AppendLine($"- Description: {subtask.Description}");
        builder.AppendLine($"- Expected outcome: {subtask.ExpectedOutcome}");
        builder.AppendLine($"- State: {subtask.State}");
        if (!string.IsNullOrWhiteSpace(subtask.Outcome))
        {
            builder.AppendLine($"- Outcome: {subtask.Outcome}");
        }

        return builder.ToString().TrimEnd();
    }
}
