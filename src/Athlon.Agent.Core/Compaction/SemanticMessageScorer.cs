namespace Athlon.Agent.Core.Compaction;

public static class SemanticMessageScorer
{
    private const int PreserveScoreThreshold = 3;

    public static int Score(ChatMessage message)
    {
        if (SummaryMessageBuilder.IsSummaryMessage(message))
        {
            return -5;
        }

        var score = message.Role switch
        {
            MessageRole.User => 3,
            MessageRole.Tool => ScoreToolMessage(message),
            MessageRole.Assistant => 0,
            _ => 0
        };

        score += ScorePathSignals(message.Content);
        if (!string.IsNullOrWhiteSpace(message.ReasoningContent))
        {
            score += ScorePathSignals(message.ReasoningContent);
        }

        return score;
    }

    public static bool ShouldPreserveInSummary(ChatMessage message) => Score(message) >= PreserveScoreThreshold;

    private static int ScoreToolMessage(ChatMessage message)
    {
        var content = message.Content ?? string.Empty;
        if (content.Contains("evicted/", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Archived at:", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (content.Contains("file_write", StringComparison.OrdinalIgnoreCase)
            || content.Contains("file_edit", StringComparison.OrdinalIgnoreCase)
            || content.Contains("execute_command", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 0;
    }

    private static int ScorePathSignals(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (text.Contains(".cs", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".tsx", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".json", StringComparison.OrdinalIgnoreCase)
            || text.Contains('\\')
            || text.Contains('/'))
        {
            return 2;
        }

        return 0;
    }
}
