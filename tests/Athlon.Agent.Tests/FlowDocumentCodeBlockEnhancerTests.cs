using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Athlon.Agent.App.Services;
using MdXaml;

namespace Athlon.Agent.Tests;

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
        return scroll?.Content is TextBlock textBlock ? textBlock.Text : string.Empty;
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
