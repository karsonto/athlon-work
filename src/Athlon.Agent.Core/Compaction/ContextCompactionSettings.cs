using System.Text.Json.Serialization;

namespace Athlon.Agent.Core.Compaction;

public sealed class ContextCompactionSettings
{
    public int ContextWindowTokens { get; set; } = 256_000;

    public double AutoCompactThresholdRatio { get; set; } = 0.80;

    public double MicrocompactAggressiveRatio { get; set; } = 0.50;

    public int MicrocompactKeepToolMessages { get; set; } = 5;

    public int MicrocompactKeepToolMessagesAggressive { get; set; } = 3;

    public int MicrocompactMinContentLength { get; set; } = 100;

    public int MaxConversationCharsForSummary { get; set; } = 200_000;

    public int SummaryMaxTokens { get; set; } = 4_096;

    [JsonIgnore]
    public int AutoCompactTokenThreshold =>
        (int)(ContextWindowTokens * AutoCompactThresholdRatio);

    [JsonIgnore]
    public int MicrocompactAggressiveTokenThreshold =>
        (int)(ContextWindowTokens * MicrocompactAggressiveRatio);
}
