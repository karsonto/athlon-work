using System.Text;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Core.Compaction;

/// <summary>Deterministic Overflow fallback when the summary LLM fails or returns empty.</summary>
public static class TemplateSummaryBuilder
{
    public static string Build(
        IReadOnlyList<ChatMessage> prefix,
        ContextCompactionSettings settings,
        string? mustPreserveAppendix)
    {
        var maxChars = Math.Max(512, ContextTokenEstimator.EstimateCharacterBudget(
            Math.Max(128, Math.Min(settings.SummaryMaxTokens, settings.SummaryMaxTokens / 2))));

        var builder = new StringBuilder();
        builder.AppendLine("## Conversation summary (template fallback)");
        builder.AppendLine();
        builder.AppendLine("### SESSION INTENT");
        AppendLatestUser(builder, prefix, settings.OverflowReattachUserMaxChars);
        builder.AppendLine();
        builder.AppendLine("### Must preserve");
        if (!string.IsNullOrWhiteSpace(mustPreserveAppendix))
        {
            builder.AppendLine(mustPreserveAppendix.Trim());
        }
        else
        {
            builder.AppendLine("None");
        }

        builder.AppendLine();
        builder.AppendLine("### Change / shell tool trail");
        AppendChangeTrail(builder, prefix);
        builder.AppendLine();
        builder.AppendLine("### NEXT STEPS");
        builder.AppendLine("Continue from the retained tail after this summary.");

        var text = builder.ToString().Trim();
        if (text.Length <= maxChars)
        {
            return text;
        }

        // Priority: keep head (intent) + must_preserve section; trim trail.
        return text[..maxChars] + "\n...(template summary truncated)";
    }

    private static void AppendLatestUser(StringBuilder builder, IReadOnlyList<ChatMessage> prefix, int maxChars)
    {
        for (var i = prefix.Count - 1; i >= 0; i--)
        {
            var message = prefix[i];
            if (message.Role != MessageRole.User
                || SummaryMessageBuilder.IsSummaryMessage(message)
                || ApprovedPlanPrompt.IsApprovedPlanMessage(message))
            {
                continue;
            }

            var content = message.Content ?? string.Empty;
            maxChars = Math.Max(256, maxChars);
            if (content.Length > maxChars)
            {
                content = content[..maxChars] + "...";
            }

            builder.AppendLine(string.IsNullOrWhiteSpace(content) ? "(empty user message)" : content.Trim());
            return;
        }

        builder.AppendLine("None");
    }

    private static void AppendChangeTrail(StringBuilder builder, IReadOnlyList<ChatMessage> prefix)
    {
        var lines = 0;
        for (var i = 0; i < prefix.Count && lines < 40; i++)
        {
            var message = prefix[i];
            if (message.Role != MessageRole.Tool && message.Role != MessageRole.Assistant)
            {
                continue;
            }

            if (!SemanticMessageScorer.ShouldPreserveInSummary(message)
                && message.Role != MessageRole.Tool)
            {
                continue;
            }

            var snippet = (message.Content ?? string.Empty)
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\n', ' ')
                .Trim();
            if (snippet.Length > 160)
            {
                snippet = snippet[..160] + "...";
            }

            if (string.IsNullOrWhiteSpace(snippet))
            {
                continue;
            }

            builder.AppendLine($"- [{message.Role}] {snippet}");
            lines++;
        }

        if (lines == 0)
        {
            builder.AppendLine("None");
        }
    }
}
