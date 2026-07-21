using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class CommandDenyListMatcherTests
{
    [Fact]
    public void IsDenied_word_entry_blocks_format_command()
    {
        Assert.True(CommandDenyListMatcher.IsDenied("format C:", ["format"]));
    }

    [Fact]
    public void IsDenied_word_entry_does_not_block_formatting()
    {
        Assert.False(CommandDenyListMatcher.IsDenied("dotnet formatting --check", ["format"]));
    }

    [Fact]
    public void IsDenied_phrase_entry_still_uses_substring_match()
    {
        Assert.True(CommandDenyListMatcher.IsDenied("cmd /c del /s /q temp", ["del /s"]));
    }
}
