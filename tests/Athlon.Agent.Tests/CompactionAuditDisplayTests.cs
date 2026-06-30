using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class CompactionAuditDisplayTests
{
    [Fact]
    public void Parse_ForceCompact_ShowsStrategyAndLayers()
    {
        var content = CompactionMessageContent.CreateConversationCompact(
            1000,
            500,
            12,
            "transcript.jsonl",
            "summary body",
            CompactionStrategy.ForceCompact,
            [CompactionLayer.TruncateArgs, CompactionLayer.ConversationCompact]);

        var display = CompactionAuditDisplay.Parse(content);

        Assert.Equal("③ 强制对话压缩", display.CardTitle);
        Assert.Contains("模型上下文超限", display.StrategySubtitle, StringComparison.Ordinal);
        Assert.Contains("② 工具参数截断", display.StrategySubtitle, StringComparison.Ordinal);
        Assert.Contains("③ LLM 对话摘要", display.StrategySubtitle, StringComparison.Ordinal);
        Assert.Equal("summary body", display.Summary);
    }

    [Fact]
    public void Parse_ManualCompact_ShowsStrategyAndLayers()
    {
        var content = CompactionMessageContent.CreateConversationCompact(
            1000,
            500,
            12,
            "transcript.jsonl",
            "summary body",
            CompactionStrategy.ManualCompact,
            [CompactionLayer.ConversationCompact]);

        var display = CompactionAuditDisplay.Parse(content);

        Assert.Equal("③ 手动对话压缩", display.CardTitle);
        Assert.Contains("用户手动压缩", display.StrategySubtitle, StringComparison.Ordinal);
        Assert.Equal("summary body", display.Summary);
    }

    [Fact]
    public void Parse_LegacyKind_FallsBackToConversationCompact()
    {
        var message = CompactionMessageContent.CreateCompactionMessage(
            "CompactionKind: conversationcompact\nTokensBefore: 1\nTokensAfter: 1\n\nSummary: legacy");

        var display = CompactionAuditDisplay.Parse(message.Content);

        Assert.Equal("③ 对话压缩（LLM 摘要）", display.CardTitle);
    }
}
