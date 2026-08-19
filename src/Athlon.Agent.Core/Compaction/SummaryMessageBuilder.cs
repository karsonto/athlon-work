namespace Athlon.Agent.Core.Compaction;

public static class SummaryMessageBuilder
{
    public static ChatMessage CreateSummaryPlaceholder(string summaryText, string? transcriptPath, bool hiddenFromTimeline = false)
    {
        var content = BuildSummaryContent(summaryText, transcriptPath, hiddenFromTimeline);
        return ChatMessage.Create(MessageRole.Summary, content);
    }

    public static bool IsSummaryMessage(ChatMessage message)
    {
        if (message.Role == MessageRole.Summary)
        {
            return true;
        }

        // Legacy sessions stored summaries as User + marker / compressed placeholder.
        if (message.Role != MessageRole.User)
        {
            return false;
        }

        return message.Content.Contains(ConversationCompactionDefaults.SummaryMessageMarker, StringComparison.Ordinal)
               || CompactionMessageContent.IsCompressedPlaceholder(message.Content);
    }

    public static bool IsHiddenSummaryMessage(ChatMessage message) =>
        message.Role == MessageRole.Summary
        && message.Content.Contains(ConversationCompactionDefaults.HiddenSummaryMessageMarker, StringComparison.Ordinal);

    public static IReadOnlyList<ChatMessage> FilterSummaryMessages(IReadOnlyList<ChatMessage> messages) =>
        messages.Where(message => !IsSummaryMessage(message)).ToList();

    private static string BuildSummaryContent(string summaryText, string? transcriptPath, bool hiddenFromTimeline)
    {
        var trimmedSummary = summaryText.Trim();
        var marker = hiddenFromTimeline
            ? ConversationCompactionDefaults.HiddenSummaryMessageMarker
            : ConversationCompactionDefaults.SummaryMessageMarker;
        if (!string.IsNullOrWhiteSpace(transcriptPath))
        {
            return
                "You are in the middle of a conversation that has been summarized.\n\n" +
                "The full conversation history has been saved to " +
                transcriptPath +
                " should you need to refer back to it for details.\n\n" +
                "A condensed summary follows:\n\n" +
                "<summary>\n" +
                trimmedSummary +
                "\n</summary>\n\n" +
                marker;
        }

        return marker + "\n" +
               "Here is a summary of the conversation to date:\n\n" +
               trimmedSummary;
    }
}
