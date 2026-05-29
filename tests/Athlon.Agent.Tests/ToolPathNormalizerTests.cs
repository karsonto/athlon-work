using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ToolPathNormalizerTests
{
    [Theory]
    [InlineData(@"src\foo\bar.cs", "src/foo/bar.cs")]
    [InlineData(@"D:\PROJECT\demo.html", "D:/PROJECT/demo.html")]
    [InlineData("src/foo.cs", "src/foo.cs")]
    public void ForModel_ReplacesBackslashesWithForwardSlashes(string input, string expected) =>
        Assert.Equal(expected, ToolPathNormalizer.ForModel(input));

    [Fact]
    public void NormalizePathArguments_NormalizesPathKeyOnly()
    {
        var arguments = new Dictionary<string, string>
        {
            ["path"] = @"a\b.txt",
            ["content"] = "line1\r\nline2"
        };

        var normalized = ToolPathNormalizer.NormalizePathArguments(arguments);

        Assert.Equal("a/b.txt", normalized["path"]);
        Assert.Equal("line1\r\nline2", normalized["content"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalizeForFileOperation_RejectsEmpty(string? path)
    {
        Assert.False(ToolPathNormalizer.TryNormalizeForFileOperation(path, out _, out var message));
        Assert.Contains("empty", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://example.com/a.txt")]
    [InlineData("file:///C:/temp/a.txt")]
    public void TryNormalizeForFileOperation_RejectsUris(string path)
    {
        Assert.False(ToolPathNormalizer.TryNormalizeForFileOperation(path, out _, out var message));
        Assert.Contains("URI", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalizeForFileOperation_NormalizesValidPaths()
    {
        Assert.True(ToolPathNormalizer.TryNormalizeForFileOperation(@"src\a\b.txt", out var path, out _));
        Assert.Equal("src/a/b.txt", path);
    }
}
