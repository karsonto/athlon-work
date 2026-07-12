using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.SlashCommands;

namespace Athlon.Agent.Tests;

public sealed class ComposerCompletionQueryTests
{
    private static readonly IComposerSlashCommandRegistry EmptySlashRegistry =
        ComposerTestFactory.CreateSlashRegistry();

    [Theory]
    [InlineData("@Sources/AthlonAgent/MainWindow.swift", 39, true)]
    public void At_query_accepts_file_mentions(string text, int caret, bool expected)
    {
        Assert.Equal(expected, ComposerCompletionQuery.TryGetAtQuerySpan(text, caret, out _, out _));
    }

    [Theory]
    [InlineData("/rea", 4, true)]
    [InlineData("/", 1, true)]
    [InlineData("use /rea", 8, true)]
    public void Slash_query_accepts_discovery_tokens(string text, int caret, bool expected)
    {
        Assert.Equal(
            expected,
            ComposerCompletionQuery.TryGetSlashQuerySpan(
                text,
                caret,
                EmptySlashRegistry,
                out _,
                out _,
                out _));
    }

    [Theory]
    [InlineData("//skill:local-audio", 21)]
    [InlineData("src/foo/bar", 11)]
    [InlineData("https://example.com", 19)]
    public void Slash_query_rejects_embedded_references_and_paths(string text, int caret)
    {
        Assert.False(ComposerCompletionQuery.TryGetSlashQuerySpan(
            text,
            caret,
            EmptySlashRegistry,
            out _,
            out _,
            out _));
    }

    [Theory]
    [InlineData("@State private var isReady = false", 37)]
    [InlineData("contact user@example.com", 24)]
    public void At_query_rejects_swift_attributes_and_email_addresses(string text, int caret)
    {
        Assert.False(ComposerCompletionQuery.TryGetAtQuerySpan(text, caret, out _, out _));
    }

    [Fact]
    public void TryGetActiveQuery_prefers_at_over_slash()
    {
        Assert.True(ComposerCompletionQuery.TryGetActiveQuery(
            "see @rea",
            caretIndex: 8,
            EmptySlashRegistry,
            out var trigger,
            out _,
            out _,
            out var query));
        Assert.Equal(ComposerCompletionTrigger.At, trigger);
        Assert.Equal("rea", query);
    }
}
