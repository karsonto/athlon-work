using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class PlanDocumentHtmlBuilderTests
{
    [Fact]
    public void BuildDocument_IncludesTitleOverviewAndMermaidMarkup()
    {
        var html = PlanDocumentHtmlBuilder.BuildDocument(
            "Fix Race",
            "Gate SyncChatView",
            """
            ## Steps
            ```mermaid
            flowchart LR
              a[Hidden] --> b[Displayed]
            ```
            """);

        Assert.Contains("Fix Race", html, StringComparison.Ordinal);
        Assert.Contains("Gate SyncChatView", html, StringComparison.Ordinal);
        Assert.Contains("class=\"mermaid\"", html, StringComparison.Ordinal);
        Assert.Contains("flowchart LR", html, StringComparison.Ordinal);
        Assert.Contains("mermaid.min.js", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDocument_WithoutMermaid_StillRendersMarkdown()
    {
        var html = PlanDocumentHtmlBuilder.BuildDocument(
            "Simple",
            "One step",
            "## Only text\n\n- item");

        Assert.Contains("Simple", html, StringComparison.Ordinal);
        Assert.Contains("Only text", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"mermaid\"", html, StringComparison.Ordinal);
    }
}
