using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ToolCallStreamingJsonHelperTests
{
    [Fact]
    public void TryExtractStringProperty_reads_path_from_partial_json()
    {
        const string partial = """{"path":"src/App.tsx","content":"hel""";

        Assert.True(ToolCallStreamingJsonHelper.TryExtractStringProperty(partial, "path", out var path));
        Assert.Equal("src/App.tsx", path);
    }

    [Fact]
    public void TryEstimateStringPropertyLength_counts_partial_content()
    {
        const string partial = """{"path":"a.ts","content":"hel""";

        Assert.True(ToolCallStreamingJsonHelper.TryEstimateStringPropertyLength(partial, "content", out var length));
        Assert.Equal(3, length);
    }

    [Fact]
    public void TryEstimateStringPropertyLength_ignores_escaped_quotes()
    {
        const string partial = """{"path":"a.ts","content":"a\"b""";

        Assert.True(ToolCallStreamingJsonHelper.TryEstimateStringPropertyLength(partial, "content", out var length));
        Assert.Equal(3, length);
    }

    [Fact]
    public void TryParseCompleteFileWriteArgs_returns_exact_content_length()
    {
        const string json = """{"path":"src/App.tsx","content":"hello\nworld"}""";

        Assert.True(ToolCallStreamingJsonHelper.TryParseCompleteFileWriteArgs(json, out var path, out var content));
        Assert.Equal("src/App.tsx", path);
        Assert.Equal("hello\nworld", content);
        Assert.Equal(11, content.Length);
    }
}
