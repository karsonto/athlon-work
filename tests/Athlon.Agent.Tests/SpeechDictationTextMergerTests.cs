using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SpeechDictationTextMergerTests
{
    [Fact]
    public void Compose_JoinsBaseCommittedAndInterim()
    {
        var text = SpeechDictationTextMerger.Compose("已有", "确认", "预览");

        Assert.Equal("已有确认预览", text);
    }

    [Theory]
    [InlineData("", "hello", "hello")]
    [InlineData("hello", "world", "hello world")]
    [InlineData("你好", "世界", "你好世界")]
    [InlineData("hello", "", "hello")]
    public void AppendFinalSegment_InsertsAsciiSeparatorOnlyWhenNeeded(
        string committed,
        string finalSegment,
        string expected)
    {
        var text = SpeechDictationTextMerger.AppendFinalSegment(committed, finalSegment);

        Assert.Equal(expected, text);
    }
}
