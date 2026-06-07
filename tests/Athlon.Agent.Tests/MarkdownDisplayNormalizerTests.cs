using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class MarkdownDisplayNormalizerTests
{
    [Fact]
    public void NormalizeForDisplay_removes_rules_adjacent_to_fenced_code_blocks()
    {
        const string markdown = """
            ## 目录结构

            ---

            ```text
            src/
            ├── Athlon.Agent.Core/Prompt/
            ```

            ---
            """;

        var normalized = MarkdownDisplayNormalizer.NormalizeForDisplay(markdown);

        Assert.Contains("## 目录结构", normalized, StringComparison.Ordinal);
        Assert.Contains("```text", normalized, StringComparison.Ordinal);
        Assert.Contains("src/", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("---", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeForDisplay_keeps_standalone_rule()
    {
        const string markdown = """
            section one

            ---

            section two
            """;

        var normalized = MarkdownDisplayNormalizer.NormalizeForDisplay(markdown);

        Assert.Contains("section one", normalized, StringComparison.Ordinal);
        Assert.Contains("---", normalized, StringComparison.Ordinal);
        Assert.Contains("section two", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFencedBlocks_parses_language_and_content()
    {
        const string markdown = """
            intro

            ```csharp
            var x = 1;
            ```

            ```text
            src/
            ```
            """;

        var blocks = MarkdownDisplayNormalizer.ExtractFencedBlocks(markdown);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("csharp", blocks[0].Language);
        Assert.Contains("var x = 1;", blocks[0].Content, StringComparison.Ordinal);
        Assert.Equal("text", blocks[1].Language);
        Assert.Contains("src/", blocks[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFencedBlocks_handles_unclosed_fence_for_streaming()
    {
        const string markdown = """
            ```text
            src/
            ├── still streaming
            """;

        var blocks = MarkdownDisplayNormalizer.ExtractFencedBlocks(markdown);

        Assert.Single(blocks);
        Assert.Equal("text", blocks[0].Language);
        Assert.Contains("still streaming", blocks[0].Content, StringComparison.Ordinal);
    }
}
