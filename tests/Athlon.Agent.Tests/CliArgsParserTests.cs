using Athlon.Agent.Core.Cli;

namespace Athlon.Agent.Tests;

public sealed class CliArgsParserTests
{
    [Fact]
    public void Parse_EmptyArgs_OpensRepl()
    {
        var options = CliArgsParser.Parse([]);
        Assert.False(options.Once);
        Assert.False(options.Yes);
        Assert.Null(options.SessionId);
        Assert.Null(options.Prompt);
    }

    [Fact]
    public void Parse_PromptAndFlags()
    {
        var options = CliArgsParser.Parse(["--once", "--yes", "--session", "abc", "解释", "这个项目"]);
        Assert.True(options.Once);
        Assert.True(options.Yes);
        Assert.Equal("abc", options.SessionId);
        Assert.Equal("解释 这个项目", options.Prompt);
    }

    [Theory]
    [InlineData("/exit", CliReplCommandKind.Exit)]
    [InlineData("/QUIT", CliReplCommandKind.Exit)]
    [InlineData("/new", CliReplCommandKind.New)]
    [InlineData("  ", CliReplCommandKind.Empty)]
    [InlineData("hello", CliReplCommandKind.Message)]
    public void ReplCommand_Parses(string line, CliReplCommandKind expected)
    {
        Assert.Equal(expected, CliReplCommand.Parse(line));
    }
}
