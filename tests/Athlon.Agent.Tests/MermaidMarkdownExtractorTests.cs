using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class MermaidMarkdownExtractorTests
{
    [Fact]
    public void ContainsMermaid_detects_fenced_block()
    {
        const string markdown = """
            intro

            ```mermaid
            sequenceDiagram
              A->>B: hi
            ```

            tail
            """;

        Assert.True(MermaidMarkdownExtractor.ContainsMermaid(markdown));
    }

    [Fact]
    public void ExtractDiagrams_returns_trimmed_source()
    {
        const string markdown = """
            ```mermaid
            flowchart LR
              A --> B
            ```
            """;

        var diagrams = MermaidMarkdownExtractor.ExtractDiagrams(markdown);

        Assert.Single(diagrams);
        Assert.Contains("flowchart LR", diagrams[0], StringComparison.Ordinal);
        Assert.Contains("A --> B", diagrams[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractDiagrams_supports_multiple_blocks()
    {
        const string markdown = """
            ```mermaid
            graph TD
              X
            ```
            ```mermaid
            sequenceDiagram
              A->>B: ok
            ```
            """;

        var diagrams = MermaidMarkdownExtractor.ExtractDiagrams(markdown);

        Assert.Equal(2, diagrams.Count);
    }
}
