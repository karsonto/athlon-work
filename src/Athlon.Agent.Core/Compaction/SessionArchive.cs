namespace Athlon.Agent.Core.Compaction;

public sealed record SessionTranscriptInfo(string FileName, DateTimeOffset WrittenAt, int MessageCount);

public sealed record SessionTranscriptMatch(string FileName, int Offset, string Preview);

public static class SessionArchiveLimits
{
    public const int MaxToolOutputTokens = 10_000;

    public static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var maxChars = ContextTokenEstimator.EstimateCharacterBudget(MaxToolOutputTokens);
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars] + "\n...(truncated)";
    }
}
