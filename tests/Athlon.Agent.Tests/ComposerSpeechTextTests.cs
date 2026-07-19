using Athlon.Agent.App.Services.Speech;

namespace Athlon.Agent.Tests;

public sealed class ComposerSpeechTextTests
{
    [Theory]
    [InlineData("", "你好", "你好")]
    [InlineData("已有内容", "继续", "已有内容 继续")]
    [InlineData("已有内容 ", "继续", "已有内容 继续")]
    [InlineData("已有内容\n", "继续", "已有内容\n继续")]
    [InlineData("hello", "  world  ", "hello world")]
    [InlineData("hello", "   ", "hello")]
    public void AppendTranscript_inserts_space_when_needed(string existing, string spoken, string expected)
    {
        Assert.Equal(expected, ComposerSpeechText.AppendTranscript(existing, spoken));
    }
}
