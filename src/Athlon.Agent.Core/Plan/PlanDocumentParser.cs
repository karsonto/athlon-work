using System.Text;
using System.Text.RegularExpressions;

namespace Athlon.Agent.Core.Plan;

public static partial class PlanDocumentParser
{
    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"^##\s+(Acceptance|验收|验收标准)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AcceptanceHeadingRegex();

    [GeneratedRegex(@"^##\s+(Steps|Implementation|实现步骤|步骤)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex StepsHeadingRegex();

    [GeneratedRegex(@"^\s*[-*]\s+\[.\]\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex TodoCheckboxRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex NumberedStepRegex();

    public static bool LooksComplete(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown) || markdown.Trim().Length < 80)
        {
            return false;
        }

        var hasTitle = TitleRegex().IsMatch(markdown);
        var hasSteps = StepsHeadingRegex().IsMatch(markdown) || NumberedStepRegex().IsMatch(markdown);
        var hasAcceptance = AcceptanceHeadingRegex().IsMatch(markdown);
        return hasTitle && hasSteps && hasAcceptance;
    }

    public static string? ParseTitle(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var match = TitleRegex().Match(markdown);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public static string ComposeMarkdown(string? title, string? overview, string? body)
    {
        var sb = new StringBuilder();
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? "Plan" : title.Trim();
        sb.Append("# ").AppendLine(resolvedTitle).AppendLine();
        if (!string.IsNullOrWhiteSpace(overview))
        {
            sb.AppendLine(overview.Trim()).AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            var trimmed = body.Trim();
            if (trimmed.StartsWith('#') && TitleRegex().IsMatch(trimmed))
            {
                // Body already includes a title — use as full document when overview empty.
                if (string.IsNullOrWhiteSpace(overview))
                {
                    return trimmed + Environment.NewLine;
                }
            }

            sb.AppendLine(trimmed).AppendLine();
        }

        return sb.ToString();
    }

    public static IReadOnlyList<PlanTodoItem> ParseTodos(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var todos = new List<PlanTodoItem>();
        var index = 1;
        foreach (Match match in TodoCheckboxRegex().Matches(markdown))
        {
            var content = match.Groups[1].Value.Trim();
            if (content.Length == 0)
            {
                continue;
            }

            todos.Add(new PlanTodoItem { Id = $"todo-{index}", Content = content });
            index++;
        }

        if (todos.Count > 0)
        {
            return todos;
        }

        foreach (Match match in NumberedStepRegex().Matches(markdown))
        {
            var content = match.Groups[1].Value.Trim();
            if (content.Length == 0 || content.Length > 200)
            {
                continue;
            }

            todos.Add(new PlanTodoItem { Id = $"todo-{index}", Content = content });
            index++;
            if (todos.Count >= 12)
            {
                break;
            }
        }

        return todos;
    }

    public static string? GetLastAssistantText(AgentSession session)
    {
        for (var i = session.Messages.Count - 1; i >= 0; i--)
        {
            var message = session.Messages[i];
            if (message.Role == MessageRole.Assistant
                && !string.IsNullOrWhiteSpace(message.Content))
            {
                return message.Content;
            }
        }

        return null;
    }

    public static string FallbackMarkdownFromAssistant(string? assistantText, string? goal)
    {
        var title = string.IsNullOrWhiteSpace(goal) ? "Implementation plan" : Truncate(goal!, 80);
        var body = string.IsNullOrWhiteSpace(assistantText)
            ? "No plan content was produced. Revise with more detail."
            : assistantText.Trim();

        return ComposeMarkdown(
            title,
            "Auto-captured from the draft turn (publish_plan was not called).",
            "## Steps\n\n1. Review and refine this plan.\n\n## Acceptance\n\n- [ ] Plan reviewed and approved\n\n## Notes\n\n" + body);
    }

    private static string Truncate(string text, int max)
    {
        var compact = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= max ? compact : compact[..(max - 3)] + "...";
    }
}
