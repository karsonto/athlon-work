using System.Text;

namespace Athlon.Agent.Core.Compaction;

public static class SemanticCutoffPlanner
{
    public static int DetermineCutoffIndex(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        int keepTokenBudget)
    {
        if (conversation.Count == 0 || keepTokenBudget <= 0)
        {
            return 0;
        }

        var protectedStart = FindProtectedTailStart(conversation);
        var tokenKeepStart = FindTokenBasedTailStart(conversation, keepTokenBudget, settings.IncludeReasoningInModelContext);
        var rawCutoff = Math.Min(protectedStart, tokenKeepStart);
        return ConversationCutoffPlanner.FindSafeCutoffPoint(conversation, rawCutoff);
    }

    public static string? BuildMustPreserveAppendix(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        int keepTokenBudget)
    {
        if (!settings.DynamicCompaction.EnableSemanticCutoff || conversation.Count == 0)
        {
            return null;
        }

        var cutoff = DetermineCutoffIndex(conversation, settings, keepTokenBudget);
        if (cutoff <= 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("<must_preserve>");
        builder.AppendLine("The following facts from earlier history MUST appear in your summary:");

        for (var index = 0; index < cutoff; index++)
        {
            var message = conversation[index];
            if (!SemanticMessageScorer.ShouldPreserveInSummary(message))
            {
                continue;
            }

            builder.AppendLine($"- [{message.Role}] {TruncateForAppendix(message.Content)}");
        }

        builder.AppendLine("</must_preserve>");
        return builder.ToString().TrimEnd();
    }

    private static int FindProtectedTailStart(IReadOnlyList<ChatMessage> conversation)
    {
        for (var index = conversation.Count - 1; index >= 0; index--)
        {
            if (conversation[index].Role != MessageRole.User)
            {
                continue;
            }

            if (SummaryMessageBuilder.IsSummaryMessage(conversation[index]))
            {
                continue;
            }

            return index;
        }

        return conversation.Count;
    }

    private static int FindTokenBasedTailStart(
        IReadOnlyList<ChatMessage> conversation,
        int keepTokenBudget,
        bool includeReasoningInModelContext)
    {
        var tokensKept = 0;
        for (var index = conversation.Count - 1; index >= 0; index--)
        {
            tokensKept += ContextTokenEstimator.EstimateMessage(conversation[index], includeReasoningInModelContext);
            if (tokensKept > keepTokenBudget)
            {
                return Math.Min(conversation.Count, index + 1);
            }
        }

        return 0;
    }

    private static string TruncateForAppendix(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "(empty)";
        }

        var normalized = content.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "...";
    }
}
