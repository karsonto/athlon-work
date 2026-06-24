using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Athlon.Agent.App.Services;
using MdXaml;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class FlowDocumentCodeBlockEnhancerTests
{
    [Fact]
    public async Task Enhance_wraps_fenced_code_block_with_card_tag()
    {
        var dispatcher = await StartStaDispatcherAsync();

        await dispatcher.InvokeAsync(() =>
        {
            const string markdown = """
                ```text
                src/
                ├── core
                ```
                """;

            var engine = new Markdown { DocumentStyle = MarkdownStyle.Standard };
            var document = engine.Transform(MarkdownDisplayNormalizer.NormalizeForDisplay(markdown));
            var fencedBlocks = MarkdownDisplayNormalizer.ExtractFencedBlocks(markdown);

            FlowDocumentThemeNormalizer.Normalize(document, contextMenu: null, fencedBlocks);

            var cardContainers = document.Blocks
                .OfType<BlockUIContainer>()
                .Where(block => block.Tag as string == FlowDocumentCodeBlockEnhancer.CodeBlockCardTag)
                .ToList();

            Assert.Single(cardContainers);

            var cardState = Assert.IsType<Border>(cardContainers[0].Child).Tag as CodeBlockCardState;
            Assert.NotNull(cardState);
            Assert.Contains("src/", cardState.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Enhance_uses_fenced_source_when_flowdocument_text_is_missing()
    {
        var dispatcher = await StartStaDispatcherAsync();

        await dispatcher.InvokeAsync(() =>
        {
            const string markdown = """
                ```
                public enum ContextPressureLevel
                {
                    Normal,
                }
                ```
                """;

            var engine = new Markdown { DocumentStyle = MarkdownStyle.Standard };
            var document = engine.Transform(MarkdownDisplayNormalizer.NormalizeForDisplay(markdown));
            var fencedBlocks = MarkdownDisplayNormalizer.ExtractFencedBlocks(markdown);

            FlowDocumentThemeNormalizer.Normalize(document, contextMenu: null, fencedBlocks);

            var card = document.Blocks
                .OfType<BlockUIContainer>()
                .Single(block => block.Tag as string == FlowDocumentCodeBlockEnhancer.CodeBlockCardTag);

            var cardState = Assert.IsType<Border>(card.Child).Tag as CodeBlockCardState;
            Assert.NotNull(cardState);
            Assert.Contains("ContextPressureLevel", cardState.Text, StringComparison.Ordinal);

            var bodyText = FindCardBodyText(card.Child as Border);
            Assert.Contains("ContextPressureLevel", bodyText, StringComparison.Ordinal);
        });
    }

    private static string FindCardBodyText(Border? cardBorder)
    {
        if (cardBorder?.Child is not Grid grid)
        {
            return string.Empty;
        }

        var scroll = grid.Children.OfType<ScrollViewer>().FirstOrDefault();
        return scroll?.Content switch
        {
            TextBox textBox => textBox.Text,
            TextBlock textBlock => textBlock.Text,
            _ => string.Empty,
        };
    }

    [Fact]
    public async Task Enhance_uses_read_only_textbox_for_selectable_code_body()
    {
        var dispatcher = await StartStaDispatcherAsync();

        await dispatcher.InvokeAsync(() =>
        {
            const string markdown = """
                ```text
                selectable line
                ```
                """;

            var engine = new Markdown { DocumentStyle = MarkdownStyle.Standard };
            var document = engine.Transform(MarkdownDisplayNormalizer.NormalizeForDisplay(markdown));
            var fencedBlocks = MarkdownDisplayNormalizer.ExtractFencedBlocks(markdown);

            FlowDocumentThemeNormalizer.Normalize(document, contextMenu: null, fencedBlocks);

            var card = document.Blocks
                .OfType<BlockUIContainer>()
                .Single(block => block.Tag as string == FlowDocumentCodeBlockEnhancer.CodeBlockCardTag);

            var grid = Assert.IsType<Grid>(Assert.IsType<Border>(card.Child).Child);
            var scroll = grid.Children.OfType<ScrollViewer>().Single();
            var body = Assert.IsType<TextBox>(scroll.Content);

            Assert.True(body.IsReadOnly);
            Assert.Contains("selectable line", body.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Enhance_skips_empty_fenced_block_between_prose()
    {
        var dispatcher = await StartStaDispatcherAsync();

        await dispatcher.InvokeAsync(() =>
        {
            const string markdown = """
                Analysis text here.

                ```text

                Fix details here.
                """;

            var engine = new Markdown { DocumentStyle = MarkdownStyle.Standard };
            var document = engine.Transform(MarkdownDisplayNormalizer.NormalizeForDisplay(markdown));
            var fencedBlocks = MarkdownDisplayNormalizer.ExtractFencedBlocks(markdown);

            FlowDocumentThemeNormalizer.Normalize(document, contextMenu: null, fencedBlocks);

            Assert.DoesNotContain(
                document.Blocks,
                block => block is BlockUIContainer { Tag: var tag }
                         && tag as string == FlowDocumentCodeBlockEnhancer.CodeBlockCardTag);

            var prose = string.Join(
                '\n',
                document.Blocks.OfType<Paragraph>().Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text));
            Assert.Contains("Analysis text here.", prose, StringComparison.Ordinal);
            Assert.Contains("Fix details here.", prose, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Enhance_skips_empty_fence_pair_without_prose()
    {
        var dispatcher = await StartStaDispatcherAsync();

        await dispatcher.InvokeAsync(() =>
        {
            const string markdown = """
                ```
                ```
                """;

            var engine = new Markdown { DocumentStyle = MarkdownStyle.Standard };
            var document = engine.Transform(MarkdownDisplayNormalizer.NormalizeForDisplay(markdown));
            var fencedBlocks = MarkdownDisplayNormalizer.ExtractFencedBlocks(markdown);

            FlowDocumentThemeNormalizer.Normalize(document, contextMenu: null, fencedBlocks);

            Assert.DoesNotContain(
                document.Blocks,
                block => block is BlockUIContainer { Tag: var tag }
                         && tag as string == FlowDocumentCodeBlockEnhancer.CodeBlockCardTag);
            Assert.DoesNotContain(
                document.Blocks,
                block => block.Tag as string == "CodeBlock");
        });
    }

    [Fact]
    public async Task CodeBlockSelectionChanged_fires_when_textbox_selection_changes()
    {
        var dispatcher = await StartStaDispatcherAsync();

        await dispatcher.InvokeAsync(() =>
        {
            var fired = false;
            void Handler(object? sender, EventArgs e) => fired = true;
            FlowDocumentCodeBlockEnhancer.CodeBlockInteractionChanged += Handler;

            try
            {
                const string markdown = """
                    ```text
                    line one
                    line two
                    ```
                    """;

                var engine = new Markdown { DocumentStyle = MarkdownStyle.Standard };
                var document = engine.Transform(MarkdownDisplayNormalizer.NormalizeForDisplay(markdown));
                FlowDocumentThemeNormalizer.Normalize(document, contextMenu: null, MarkdownDisplayNormalizer.ExtractFencedBlocks(markdown));

                var card = document.Blocks
                    .OfType<BlockUIContainer>()
                    .Single(block => block.Tag as string == FlowDocumentCodeBlockEnhancer.CodeBlockCardTag);

                var grid = Assert.IsType<Grid>(Assert.IsType<Border>(card.Child).Child);
                var scroll = grid.Children.OfType<ScrollViewer>().Single();
                var body = Assert.IsType<TextBox>(scroll.Content);

                body.Select(0, 4);
                Assert.True(fired);
            }
            finally
            {
                FlowDocumentCodeBlockEnhancer.CodeBlockInteractionChanged -= Handler;
            }
        });
    }

    [Fact]
    public async Task NormalizeForDisplay_enables_mdxaml_line_break_for_single_newline()
    {
        var dispatcher = await StartStaDispatcherAsync();

        await dispatcher.InvokeAsync(() =>
        {
            const string markdown = "\"\"\"\nnext line";
            var normalized = MarkdownDisplayNormalizer.NormalizeForDisplay(markdown);

            var engine = new Markdown { DocumentStyle = MarkdownStyle.Standard };
            var document = engine.Transform(normalized);
            var paragraph = document.Blocks.OfType<Paragraph>().Single();

            Assert.Contains(paragraph.Inlines, inline => inline is LineBreak);
            var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
            Assert.Contains("\"\"\"", text, StringComparison.Ordinal);
            Assert.Contains("next line", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Normalize_softens_rule_double_to_single_border()
    {
        var dispatcher = await StartStaDispatcherAsync();

        await dispatcher.InvokeAsync(() =>
        {
            const string markdown = "above\n\n===\n\nbelow";
            var engine = new Markdown { DocumentStyle = MarkdownStyle.Standard };
            var document = engine.Transform(markdown);

            FlowDocumentThemeNormalizer.Normalize(document, contextMenu: null);

            var normalizedRule = document.Blocks
                .OfType<BlockUIContainer>()
                .Single(block => block.Tag as string == "RuleNormalized");

            Assert.IsType<Border>(normalizedRule.Child);
        });
    }

    private static Task<Dispatcher> StartStaDispatcherAsync()
    {
        var tcs = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            if (System.Windows.Application.Current is null)
            {
                var app = new global::Athlon.Agent.App.App();
                app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                Athlon.Agent.App.Themes.AppThemeManager.Apply(Athlon.Agent.App.Themes.AppThemeKind.Dark);
            }

            var dispatcher = Dispatcher.CurrentDispatcher;
            tcs.SetResult(dispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
