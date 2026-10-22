using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class TextLineEndingsTests
{
    [Fact]
    public void NormalizeToLf_CollapsesMultiCrTerminator_WithoutPhantomBlankLines()
    {
        var normalized = TextLineEndings.NormalizeToLf("alpha\r\r\r\r\nbeta\r\r\r\r\n");
        Assert.Equal("alpha\nbeta\n", normalized);
    }

    [Fact]
    public void NormalizeToLf_PreservesIntentionalBlankLines()
    {
        Assert.Equal("a\n\nb\n", TextLineEndings.NormalizeToLf("a\n\nb\n"));
        Assert.Equal("a\n\nb\n", TextLineEndings.NormalizeToLf("a\r\n\r\nb\r\n"));
    }

    [Fact]
    public void IsNonstandard_DetectsMultiCrAndLoneCr()
    {
        Assert.True(TextLineEndings.IsNonstandard("a\r\r\r\r\nb"));
        Assert.True(TextLineEndings.IsNonstandard("a\rb"));
        Assert.False(TextLineEndings.IsNonstandard("a\r\nb\r\n"));
        Assert.False(TextLineEndings.IsNonstandard("a\nb\n"));
    }

    [Fact]
    public void ToDiskText_WritesLfForNonstandard_AndCrlfForStandardCrlf()
    {
        Assert.Equal("a\nb\n", TextLineEndings.ToDiskText("a\r\r\r\r\nb\r\r\r\r\n", "a\nb\n"));
        Assert.Equal("a\r\nb\r\n", TextLineEndings.ToDiskText("a\r\nb\r\n", "a\nb\n"));
        Assert.Equal("a\nb\n", TextLineEndings.ToDiskText("a\nb\n", "a\nb\n"));
    }
}
