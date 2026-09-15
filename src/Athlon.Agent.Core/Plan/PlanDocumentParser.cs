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

    [GeneratedRegex(@"^##\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex SectionHeadingRegex();

    [GeneratedRegex(@"^\s*[-*]\s+\[.\]\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex TodoCheckboxRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex NumberedStepRegex();

    /// <summary>Upper bound on how many todos we seed from a plan so Build cannot flood the panel.</summary>
    private const int MaxTodos = 12;

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

    /// <summary>
    /// Extracts the plan's implementation todos.
    ///
    /// <para>The plan prompt requires <c>## Steps</c> (numbered) and <c>## Acceptance</c>
    /// (checklist). Acceptance items are verification criteria, not work items, so numbering
    /// inside <c>## Steps</c> is the authoritative source. Checkboxes are only a fallback for
    /// plans that omit a Steps section, and entries inside an Acceptance section are never
    /// treated as work.</para>
    /// </summary>
    public static IReadOnlyList<PlanTodoItem> ParseTodos(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var sections = SplitSections(markdown);

        // 1. Numbered steps inside an explicit Steps section — the authoritative work items.
        foreach (var section in sections)
        {
            if (!HeadingMatches(StepsHeadingRegex(), section.Heading))
            {
                continue;
            }

            var steps = Extract(NumberedStepRegex(), section.Body);
            if (steps.Count > 0)
            {
                return ToTodoItems(steps);
            }
        }

        // 2. Checkboxes outside Acceptance blocks (plans without a Steps section).
        var checkboxes = new List<string>();
        foreach (var section in sections)
        {
            if (HeadingMatches(AcceptanceHeadingRegex(), section.Heading))
            {
                continue;
            }

            checkboxes.AddRange(Extract(TodoCheckboxRegex(), section.Body, MaxTodos - checkboxes.Count));
            if (checkboxes.Count >= MaxTodos)
            {
                break;
            }
        }

        if (checkboxes.Count > 0)
        {
            return ToTodoItems(checkboxes);
        }

        // 3. Last resort: numbered items anywhere outside Acceptance blocks.
        var looseSteps = new List<string>();
        foreach (var section in sections)
        {
            if (HeadingMatches(AcceptanceHeadingRegex(), section.Heading))
            {
                continue;
            }

            looseSteps.AddRange(Extract(NumberedStepRegex(), section.Body, MaxTodos - looseSteps.Count));
            if (looseSteps.Count >= MaxTodos)
            {
                break;
            }
        }

        return ToTodoItems(looseSteps);
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

    /// <summary>
    /// Builds the safety-net plan used when a Draft turn never produced a publishable plan.
    /// The steps are derived from what the assistant actually wrote so Build does not seed the
    /// task list with placeholder work like "Review and refine this plan".
    /// </summary>
    public static string FallbackMarkdownFromAssistant(string? assistantText, string? goal)
    {
        var title = string.IsNullOrWhiteSpace(goal) ? "Implementation plan" : Truncate(goal!, 80);
        var steps = DeriveSteps(assistantText);
        var stepsBody = steps.Count == 0
            ? "1. Confirm the intended scope with the user."
            : string.Join(
                Environment.NewLine,
                steps.Select((step, index) => $"{index + 1}. {step}"));
        var notes = string.IsNullOrWhiteSpace(assistantText)
            ? "No plan content was produced. Revise with more detail."
            : assistantText.Trim();

        return ComposeMarkdown(
            title,
            "Auto-captured from the draft turn (publish_plan was not called). Confirm the steps before building.",
            "## Steps\n\n"
            + stepsBody
            + "\n\n## Acceptance\n\n- [ ] The steps above cover everything the user asked for\n- [ ] The user approves this plan\n\n## Notes\n\n"
            + notes);
    }

    private readonly record struct MarkdownSection(string Heading, string Body);

    /// <summary>
    /// Splits markdown on <c>##</c> headings. Content before the first heading is returned as a
    /// section with an empty heading; documents without headings yield a single such section.
    /// </summary>
    private static List<MarkdownSection> SplitSections(string markdown)
    {
        var sections = new List<MarkdownSection>();
        var matches = SectionHeadingRegex().Matches(markdown);
        if (matches.Count == 0)
        {
            sections.Add(new MarkdownSection(string.Empty, markdown));
            return sections;
        }

        if (matches[0].Index > 0)
        {
            sections.Add(new MarkdownSection(string.Empty, markdown[..matches[0].Index]));
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var start = match.Index + match.Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            sections.Add(new MarkdownSection(match.Groups[1].Value.Trim(), markdown[start..end]));
        }

        return sections;
    }

    /// <summary>
    /// Re-tests a section heading against a heading regex. The regexes anchor on <c>##</c> with
    /// <see cref="RegexOptions.Multiline"/>, so the captured heading is prefixed back on.
    /// </summary>
    private static bool HeadingMatches(Regex regex, string heading) =>
        heading.Length > 0 && regex.IsMatch("## " + heading);

    /// <summary>Collects the capture group 1 values of <paramref name="regex"/> in <paramref name="body"/>.</summary>
    private static List<string> Extract(Regex regex, string body, int max = MaxTodos)
    {
        var contents = new List<string>();
        if (max <= 0)
        {
            return contents;
        }

        foreach (Match match in regex.Matches(body))
        {
            var content = match.Groups[1].Value.Trim();
            if (content.Length == 0 || content.Length > 200)
            {
                continue;
            }

            contents.Add(content);
            if (contents.Count >= max)
            {
                break;
            }
        }

        return contents;
    }

    private static List<PlanTodoItem> ToTodoItems(IReadOnlyList<string> contents) =>
        contents
            .Select((content, index) => new PlanTodoItem { Id = $"todo-{index + 1}", Content = content })
            .ToList();

    /// <summary>
    /// Turns loose assistant prose into candidate steps: list items first, then plain lines.
    /// </summary>
    private static List<string> DeriveSteps(string? assistantText)
    {
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return [];
        }

        var steps = new List<string>();
        var lines = assistantText.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            var content = StripListMarker(line);
            if (content.Length == 0 || content.Length > 200)
            {
                continue;
            }

            if (steps.Any(existing => string.Equals(existing, content, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            steps.Add(content);
            if (steps.Count >= MaxTodos)
            {
                break;
            }
        }

        return steps;
    }

    private static string StripListMarker(string line)
    {
        var numbered = NumberedStepRegex().Match(line);
        if (numbered.Success)
        {
            return numbered.Groups[1].Value.Trim();
        }

        var checkbox = TodoCheckboxRegex().Match(line);
        if (checkbox.Success)
        {
            return checkbox.Groups[1].Value.Trim();
        }

        return line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)
            ? line[2..].Trim()
            : line;
    }

    private static string Truncate(string text, int max)
    {
        var compact = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= max ? compact : compact[..(max - 3)] + "...";
    }
}
