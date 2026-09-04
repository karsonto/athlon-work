using System.Text;

namespace Athlon.Agent.Core.Plan;

/// <summary>A single selectable option inside an <see cref="UserQuestionItem"/>.</summary>
public sealed class UserQuestionOption
{
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";

    public UserQuestionOption Clone() => new()
    {
        Id = Id,
        Label = Label
    };
}

/// <summary>One multiple-choice question rendered in the composer QuestionBar.</summary>
public sealed class UserQuestionItem
{
    public string Id { get; set; } = "";

    public string Prompt { get; set; } = "";

    public List<UserQuestionOption> Options { get; set; } = [];

    public bool AllowMultiple { get; set; }

    public UserQuestionItem Clone() => new()
    {
        Id = Id,
        Prompt = Prompt,
        AllowMultiple = AllowMultiple,
        Options = Options.Select(o => o.Clone()).ToList()
    };
}

/// <summary>
/// A question set the agent asked via the <c>ask_user</c> tool. Held in process
/// memory (never persisted) until the user answers in the QuestionBar.
/// </summary>
public sealed class UserQuestion
{
    public string RequestId { get; set; } = "";

    public List<UserQuestionItem> Questions { get; set; } = [];

    public bool AllowFreeText { get; set; } = true;

    public UserQuestion Clone() => new()
    {
        RequestId = RequestId,
        AllowFreeText = AllowFreeText,
        Questions = Questions.Select(q => q.Clone()).ToList()
    };

    public static string FormatUserAnswer(
        UserQuestion question,
        IReadOnlyDictionary<string, IReadOnlyList<string>> selections,
        string? freeText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Clarification answers:");
        foreach (var item in question.Questions)
        {
            sb.Append("- ").Append(item.Prompt).Append(": ");
            if (!selections.TryGetValue(item.Id, out var ids) || ids.Count == 0)
            {
                sb.AppendLine("(no option selected)");
                continue;
            }

            var labels = ids
                .Select(id => item.Options.FirstOrDefault(o =>
                    string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase))?.Label ?? id)
                .ToList();
            sb.AppendLine(string.Join(", ", labels));
        }

        if (!string.IsNullOrWhiteSpace(freeText))
        {
            sb.Append("- Additional notes: ").AppendLine(freeText.Trim());
        }

        return sb.ToString().TrimEnd();
    }
}
