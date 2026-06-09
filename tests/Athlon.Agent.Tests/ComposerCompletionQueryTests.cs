using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class ComposerCompletionQueryTests
{
    [Theory]
    [InlineData("/compact", 8, true)]
    [InlineData("/help", 5, true)]
    [InlineData("  /compact", 10, true)]
    public void Slash_query_accepts_composer_commands(string text, int caret, bool expected)
    {
        Assert.Equal(expected, ComposerCompletionQuery.TryGetSlashQuerySpan(text, caret, out _, out _));
    }

    [Theory]
    [InlineData("/Users/runner/work/athlon-agent/AthlonAgent/Features/MainShell/ChatPageView.swift:95:17: error")]
    [InlineData("see /Users/runner/work/athlon-agent/path")]
    [InlineData("https://example.com/path")]
    public void Slash_query_rejects_unix_paths_and_urls(string text)
    {
        var caret = text.Length;
        Assert.False(ComposerCompletionQuery.TryGetSlashQuerySpan(text, caret, out _, out _));
    }

    [Theory]
    [InlineData("@Sources/AthlonAgent/MainWindow.swift", 39, true)]
    [InlineData("@skill:local-audio", 18, true)]
    public void At_query_accepts_file_and_skill_mentions(string text, int caret, bool expected)
    {
        Assert.Equal(expected, ComposerCompletionQuery.TryGetAtQuerySpan(text, caret, out _, out _));
    }

    [Theory]
    [InlineData("@State private var isReady = false", 37)]
    [InlineData("contact user@example.com", 24)]
    public void At_query_rejects_swift_attributes_and_email_addresses(string text, int caret)
    {
        Assert.False(ComposerCompletionQuery.TryGetAtQuerySpan(text, caret, out _, out _));
    }
}
