namespace Athlon.Agent.Core.Compaction;

public static class SummaryMessageBuilder
{
    public static ChatMessage CreateSummaryPlaceholder(string summaryText, string? transcriptPath)
    {
        var content = string.IsNullOrWhiteSpace(transcriptPath)
            ? BuildSummaryContent(summaryText)
            : $"{BuildSummaryContent(summaryText)}\n\n{CompactionMessageContent.CompressedTranscriptPrefix}{transcriptPath}";

        return ChatMessage.Create(
            MessageRole.User,
            content);
    }

    public static bool IsSummaryMessage(ChatMessage message)
    {
        if (message.Role != MessageRole.User)
        {
            return false;
        }

        return message.Content.StartsWith(SummaryMarkerLine, StringComparison.Ordinal)
               || CompactionMessageContent.IsCompressedPlaceholder(message.Content);
    }

    public static IReadOnlyList<ChatMessage> FilterSummaryMessages(IReadOnlyList<ChatMessage> messages)
    {
        return messages.Where(m => !IsSummaryMessage(m)).ToList();
    }

    private const string SummaryMarkerLine = ConversationCompactionDefaults.SummaryMessageMarker + "\n";

    private static string BuildSummaryContent(string summaryText)
    {
        return $"{ConversationCompactionDefaults.SummaryMessageMarker}\n{summaryText.Trim()}";
    }
}
