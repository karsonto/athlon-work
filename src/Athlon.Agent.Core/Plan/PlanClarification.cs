using System.Text;

namespace Athlon.Agent.Core.Plan;

public sealed class PlanClarificationOption
{
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";
}

public sealed class PlanClarificationQuestion
{
    public string Id { get; set; } = "";

    public string Prompt { get; set; } = "";

    public List<PlanClarificationOption> Options { get; set; } = [];

    public bool AllowMultiple { get; set; }

    public PlanClarificationQuestion Clone() => new()
    {
        Id = Id,
        Prompt = Prompt,
        AllowMultiple = AllowMultiple,
        Options = Options
            .Select(o => new PlanClarificationOption { Id = o.Id, Label = o.Label })
            .ToList()
    };
}

public sealed class PlanClarification
{
    public string RequestId { get; set; } = "";

    public List<PlanClarificationQuestion> Questions { get; set; } = [];

    public bool AllowFreeText { get; set; } = true;

    public PlanClarification Clone() => new()
    {
        RequestId = RequestId,
        AllowFreeText = AllowFreeText,
        Questions = Questions.Select(q => q.Clone()).ToList()
    };

    public static string FormatUserAnswer(
        PlanClarification clarification,
        IReadOnlyDictionary<string, IReadOnlyList<string>> selections,
        string? freeText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Plan clarification answers:");
        foreach (var question in clarification.Questions)
        {
            sb.Append("- ").Append(question.Prompt).Append(": ");
            if (!selections.TryGetValue(question.Id, out var ids) || ids.Count == 0)
            {
                sb.AppendLine("(no option selected)");
                continue;
            }

            var labels = ids
                .Select(id => question.Options.FirstOrDefault(o =>
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
