using Athlon.Agent.App.Services;
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
        Assert.Equal(1, display.TokensBefore);
        Assert.Equal(1, display.TokensAfter);
    }

    [Fact]
    public void Parse_ReadsTokenCountsForCollapsedHeadline()
    {
        var content = CompactionMessageContent.CreateConversationCompact(
            12_300,
            5_200,
            12,
            null,
            "summary body",
            CompactionStrategy.ConversationCompact);

        var display = CompactionAuditDisplay.Parse(content);

        Assert.Equal(12_300, display.TokensBefore);
        Assert.Equal(5_200, display.TokensAfter);
        Assert.Equal(12, display.OriginalMessageCount);
        Assert.Equal(CompactionStrategy.ConversationCompact, display.Strategy);
        Assert.Equal("12.3K → 5.2K", CompactionCheckpointCopy.FormatTokenRange(display.TokensBefore, display.TokensAfter));
        Assert.Contains("12.3K", CompactionCheckpointCopy.FormatTitle(display, running: false), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatTitle_ForceCompact_UsesOverflowCopy()
    {
        var content = CompactionMessageContent.CreateConversationCompact(
            1000,
            500,
            4,
            null,
            "summary body",
            CompactionStrategy.ForceCompact);
        var display = CompactionAuditDisplay.Parse(content);
        var title = CompactionCheckpointCopy.FormatTitle(display, running: false);

        Assert.True(
            title.Contains("上下文已满", StringComparison.Ordinal)
            || title.Contains("Context full", StringComparison.Ordinal));
        Assert.DoesNotContain("③", title, StringComparison.Ordinal);
        Assert.Contains(TokenCountDisplay.FormatCompact(1000), title, StringComparison.Ordinal);
    }
}
