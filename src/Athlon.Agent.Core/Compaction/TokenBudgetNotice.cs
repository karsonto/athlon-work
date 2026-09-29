using System.Text;

namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Short per-request budget line. Kept out of runtime-context fingerprints so a changing
/// remainder does not emit "Runtime context updated."
/// </summary>
public static class TokenBudgetNotice
{
    public static string Format(ContextBudgetSnapshot budget, ContextPressureLevel pressure)
    {
        var remaining = Math.Max(0, budget.UsablePromptWindow - budget.EstimatedTotalPrompt);
        var builder = new StringBuilder();
        builder.Append("<token_budget>\n");
        builder.Append("You have ").Append(remaining)
            .Append(" tokens left in this context window. Pressure: ")
            .Append(pressure)
            .Append(".\n");
        builder.Append("</token_budget>");
        if (pressure is ContextPressureLevel.High or ContextPressureLevel.Critical or ContextPressureLevel.Overflow)
        {
            builder.Append(
                "\nContext is tight. Call session_note_append with the current goal, confirmed decisions, failed paths, and next step before more large tool output. If this phase can close, call request_context_compact.");
        }

        return builder.ToString();
    }
}
