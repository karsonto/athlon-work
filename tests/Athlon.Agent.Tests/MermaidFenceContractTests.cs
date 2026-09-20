using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

/// <summary>
/// Pins the contract between the backend markdown renderer and the timeline's mermaid selector.
/// Markdig emits <c>&lt;pre class="mermaid"&gt;…&lt;/pre&gt;</c> for a ```mermaid fence — not the
/// <c>&lt;pre&gt;&lt;code class="language-mermaid"&gt;</c> shape that hand-written HTML uses. A
/// selector that only understands the latter leaves every assistant diagram as unstyled raw text.
/// </summary>
public sealed class MermaidFenceContractTests
{
    [Fact]
    public void MarkdownRenderer_emits_bare_pre_mermaid_for_mermaid_fence()
    {
        const string markdown = """
            ```mermaid
            flowchart LR
              A[mvn clean test] --> B[编译 main + test<br/>source/target 1.8]
            ```
            """;

        var html = MarkdownHtmlRenderer.ToHtmlFragment(markdown);

        Assert.Contains("<pre class=\"mermaid\">", html, StringComparison.Ordinal);
        // The diagram source must stay intact, including HTML-ish <br/> used for line breaks.
        Assert.Contains("&lt;br/>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("language-mermaid", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownRenderer_escapes_diagram_source_so_it_cannot_inject_markup()
    {
        const string markdown = """
            ```mermaid
            flowchart LR
              A[<img src=x onerror=alert(1)>] --> B[end]
            ```
            """;

        var html = MarkdownHtmlRenderer.ToHtmlFragment(markdown);

        Assert.Contains("<pre class=\"mermaid\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TimelineSelector_matches_the_bare_pre_mermaid_shape_markdig_produces()
    {
        var timelineJs = ReadChatAsset("chat-timeline.js");

        // Both shapes must be matched: Markdig's bare pre, and the user-bubble code child.
        Assert.Contains("'pre.mermaid, pre > code.language-mermaid'", timelineJs, StringComparison.Ordinal);
        // Source lives on the pre for one shape and on the code child for the other.
        Assert.Contains("function mermaidSourceOf", timelineJs, StringComparison.Ordinal);
        // A bare pre.mermaid has no <code> child, so the highlighter must skip it explicitly
        // instead of bailing out of the whole enhancement pass.
        Assert.Contains("pre.classList.contains('mermaid')", timelineJs, StringComparison.Ordinal);
    }

    private static readonly string[] TimelineModules =
    [
        "timeline-state.js",
        "timeline-render.js",
        "timeline-cards.js",
        "timeline-protocol.js"
    ];

    private static string ReadChatAsset(string name)
    {
        // The timeline script was split into focused modules; tests keep reading it as one source.
        if (string.Equals(name, "chat-timeline.js", StringComparison.Ordinal))
        {
            var combined = new System.Text.StringBuilder();
            foreach (var module in TimelineModules)
            {
                combined.Append(ReadChatAsset(module));
            }

            return combined.ToString();
        }

        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Chat");
        return File.ReadAllText(Path.Combine(dir, name));
    }
}
