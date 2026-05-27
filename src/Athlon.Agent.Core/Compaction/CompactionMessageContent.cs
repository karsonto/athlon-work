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
        string summaryPreview)
    {
        var summary = string.IsNullOrWhiteSpace(summaryPreview)
            ? $"已将 {originalMessageCount} 条消息压缩为摘要并保留最近上下文。"
            : summaryPreview.Trim();
        if (summary.Length > 200)
        {
            summary = summary[..200] + "...";
        }

        return Build(
            CompactionKind.ConversationCompact,
            tokensBefore,
            tokensAfter,
            summary,
            originalMessageCount: originalMessageCount,
            transcriptPath: transcriptPath);
    }

    public static bool IsSummaryPlaceholder(string content) =>
        content.StartsWith(ConversationCompactionDefaults.SummaryMessageMarker, StringComparison.Ordinal)
        || IsCompressedPlaceholder(content);

    [Obsolete("Microcompact removed; kept for legacy audit parsing.")]
    public static string CreateMicrocompact(
        int tokensBefore,
        int tokensAfter,
        int clearedToolMessages,
        int keepToolMessages)
    {
        var summary =
            $"已清理 {clearedToolMessages} 条较早的工具输出，保留最近 {keepToolMessages} 条完整内容。";
        return Build(
            CompactionKind.Microcompact,
            tokensBefore,
            tokensAfter,
            summary,
            clearedToolMessages: clearedToolMessages,
            keepToolMessages: keepToolMessages);
    }

    public static string CreateAutoCompact(
        int tokensBefore,
        int tokensAfter,
        int originalMessageCount,
        string transcriptPath,
        string summaryPreview)
    {
        var summary = string.IsNullOrWhiteSpace(summaryPreview)
            ? $"已将 {originalMessageCount} 条消息压缩为摘要。"
            : summaryPreview.Trim();
        if (summary.Length > 200)
        {
            summary = summary[..200] + "...";
        }

        return Build(
            CompactionKind.AutoCompact,
            tokensBefore,
            tokensAfter,
            summary,
            originalMessageCount: originalMessageCount,
            transcriptPath: transcriptPath);
    }

    public static string CreateManualCompact(
        int tokensBefore,
        int tokensAfter,
        int originalMessageCount,
        string transcriptPath,
        string summaryPreview) =>
        Build(
            CompactionKind.ManualCompact,
            tokensBefore,
            tokensAfter,
            string.IsNullOrWhiteSpace(summaryPreview)
                ? $"已手动压缩 {originalMessageCount} 条消息。"
                : summaryPreview.Trim().Length > 200
                    ? summaryPreview.Trim()[..200] + "..."
                    : summaryPreview.Trim(),
            originalMessageCount: originalMessageCount,
            transcriptPath: transcriptPath);

    public static ChatMessage CreateCompactionMessage(string content, string? parentId = null) =>
        ChatMessage.Create(MessageRole.Compaction, content, parentId);

    private static string Build(
        CompactionKind kind,
        int tokensBefore,
        int tokensAfter,
        string summary,
        int? clearedToolMessages = null,
        int? keepToolMessages = null,
        int? originalMessageCount = null,
        string? transcriptPath = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"CompactionKind: {kind.ToString().ToLowerInvariant()}");
        builder.AppendLine($"TokensBefore: {tokensBefore}");
        builder.AppendLine($"TokensAfter: {tokensAfter}");

        if (clearedToolMessages.HasValue)
        {
            builder.AppendLine($"ClearedToolMessages: {clearedToolMessages.Value}");
        }

        if (keepToolMessages.HasValue)
        {
            builder.AppendLine($"KeepToolMessages: {keepToolMessages.Value}");
        }

        if (originalMessageCount.HasValue)
        {
            builder.AppendLine($"OriginalMessageCount: {originalMessageCount.Value}");
        }

        if (!string.IsNullOrWhiteSpace(transcriptPath))
        {
            builder.AppendLine($"TranscriptPath: {transcriptPath}");
        }

        builder.AppendLine();
        builder.Append($"Summary: {summary}");
        return builder.ToString();
    }
}
