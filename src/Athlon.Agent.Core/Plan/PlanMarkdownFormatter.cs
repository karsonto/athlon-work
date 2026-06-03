using System.Text;

namespace Athlon.Agent.Core.Plan;

public static class PlanMarkdownFormatter
{
    public static string ToMarkdown(AgentPlan plan, bool detailed)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Plan: {plan.Name}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(plan.Description))
        {
            builder.AppendLine($"> {plan.Description.Trim()}");
            builder.AppendLine();
        }

        AppendSection(builder, "Overview", plan.Overview);
        AppendSection(builder, "Architecture", plan.Architecture);
        AppendMermaidSection(builder, plan.Mermaid);
        AppendSection(builder, "Testing Strategy", plan.TestingStrategy);
        AppendSection(builder, "Out of Scope", plan.OutOfScope);

        if (!string.IsNullOrWhiteSpace(plan.ExpectedOutcome))
        {
            AppendSection(builder, "Expected Outcome", plan.ExpectedOutcome);
        }

        if (plan.Subtasks.Count == 0)
        {
            builder.AppendLine("_No implementation steps._");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine(detailed ? "## Implementation Plan" : "## Subtasks");
        builder.AppendLine();

        for (var index = 0; index < plan.Subtasks.Count; index++)
        {
            var subtask = plan.Subtasks[index];
            builder.AppendLine(
                detailed
                    ? FormatSubtaskDetailed(index, subtask)
                    : FormatSubtaskOneLine(subtask));
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder builder, string title, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine(body.Trim());
        builder.AppendLine();
    }

    private static void AppendMermaidSection(StringBuilder builder, string? mermaid)
    {
        if (string.IsNullOrWhiteSpace(mermaid))
        {
            return;
        }

        builder.AppendLine("## Architecture Diagram");
        builder.AppendLine();
        builder.AppendLine("```mermaid");
        builder.AppendLine(mermaid.Trim());
        builder.AppendLine("```");
        builder.AppendLine();
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
        builder.AppendLine($"### {index + 1}. {FormatSubtaskOneLine(subtask)}");

        if (subtask.Files.Count > 0)
        {
            builder.AppendLine("- **Files:** " + string.Join(", ", subtask.Files.Select(file => $"`{file}`")));
        }

        builder.AppendLine($"- **Description:** {subtask.Description}");
        builder.AppendLine($"- **Acceptance:** {subtask.ExpectedOutcome}");
        builder.AppendLine($"- **State:** {subtask.State}");

        if (!string.IsNullOrWhiteSpace(subtask.Outcome))
        {
            builder.AppendLine($"- **Outcome:** {subtask.Outcome}");
        }

        return builder.ToString().TrimEnd();
    }
}
