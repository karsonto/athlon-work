using System.Text;

namespace Athlon.Agent.Core.Compaction;

public static class CompactionMessageContent
{
    public const string CompressedTranscriptPrefix = "[Compressed. Transcript:";

    public static bool IsCompressedPlaceholder(string content) =>
        content.StartsWith(CompressedTranscriptPrefix, StringComparison.Ordinal);

    public static string CreateConversationCompact(
        int tokensBefore,
        int tokensAfter,
        int originalMessageCount,
        string? transcriptPath,
        string summaryPreview,
        CompactionStrategy strategy = CompactionStrategy.ConversationCompact,
        IReadOnlyList<CompactionLayer>? layers = null,
        ContextPressureLevel? pressureLevel = null,
        double? utilization = null,
        int? summaryInputCharsBefore = null,
        int? summaryInputCharsAfter = null,
        int? hygieneSavingsEstimate = null)
    {
        var summary = string.IsNullOrWhiteSpace(summaryPreview)
            ? $"已将 {originalMessageCount} 条消息压缩为摘要并保留最近上下文。"
            : summaryPreview.Trim();

        return Build(
            CompactionKind.ConversationCompact,
            tokensBefore,
            tokensAfter,
            summary,
            strategy,
            layers,
            originalMessageCount: originalMessageCount,
            transcriptPath: transcriptPath,
            pressureLevel: pressureLevel,
            utilization: utilization,
            summaryInputCharsBefore: summaryInputCharsBefore,
            summaryInputCharsAfter: summaryInputCharsAfter,
            hygieneSavingsEstimate: hygieneSavingsEstimate);
    }

    public static bool IsSummaryPlaceholder(string content) =>
        content.StartsWith(ConversationCompactionDefaults.SummaryMessageMarker, StringComparison.Ordinal)
        || IsCompressedPlaceholder(content);

    public static ChatMessage CreateCompactionMessage(string content, string? parentId = null) =>
        ChatMessage.Create(MessageRole.Compaction, content, parentId);

    private static string Build(
        CompactionKind kind,
        int tokensBefore,
        int tokensAfter,
        string summary,
        CompactionStrategy? strategy = null,
        IReadOnlyList<CompactionLayer>? layers = null,
        int? originalMessageCount = null,
        string? transcriptPath = null,
        ContextPressureLevel? pressureLevel = null,
        double? utilization = null,
        int? summaryInputCharsBefore = null,
        int? summaryInputCharsAfter = null,
        int? hygieneSavingsEstimate = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"CompactionKind: {kind.ToString().ToLowerInvariant()}");
        if (strategy is not null)
        {
            builder.AppendLine($"CompactionStrategy: {CompactionAuditDisplay.FormatStrategy(strategy.Value)}");
        }

        if (layers is { Count: > 0 })
        {
            builder.AppendLine($"CompactionLayers: {CompactionAuditDisplay.FormatLayers(layers)}");
        }

        builder.AppendLine($"TokensBefore: {tokensBefore}");
        builder.AppendLine($"TokensAfter: {tokensAfter}");

        if (originalMessageCount.HasValue)
        {
            builder.AppendLine($"OriginalMessageCount: {originalMessageCount.Value}");
        }

        if (!string.IsNullOrWhiteSpace(transcriptPath))
        {
            builder.AppendLine($"TranscriptPath: {transcriptPath}");
        }

        if (pressureLevel is not null)
        {
            builder.AppendLine($"ContextPressure: {pressureLevel}");
        }

        if (utilization.HasValue)
        {
            builder.AppendLine($"ContextUtilization: {utilization.Value:0.###}");
        }

        if (summaryInputCharsBefore.HasValue)
        {
            builder.AppendLine($"SummaryInputCharsBefore: {summaryInputCharsBefore.Value}");
        }

        if (summaryInputCharsAfter.HasValue)
        {
            builder.AppendLine($"SummaryInputCharsAfter: {summaryInputCharsAfter.Value}");
        }

        if (hygieneSavingsEstimate is > 0)
        {
            builder.AppendLine($"HygieneSavingsEstimate: {hygieneSavingsEstimate.Value}");
        }

        builder.AppendLine();
        builder.Append($"Summary: {summary}");
        return builder.ToString();
    }
}
