using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Athlon.Agent.App.Services;

public sealed class CodeBlockCardState
{
    public required string Text { get; init; }
}

public static class FlowDocumentCodeBlockEnhancer
{
    public const string CodeBlockCardTag = "CodeBlockCard";

    /// <summary>Raised when selection changes inside a code-block card body TextBox.</summary>
    public static event EventHandler? CodeBlockInteractionChanged;

    public static void Enhance(FlowDocument document, IReadOnlyList<FencedBlockInfo>? fencedBlocks)
    {
        if (document.Blocks.Count == 0)
        {
            return;
        }

        if (document.Tag as string == CodeBlockCardTag)
        {
            return;
        }

        var candidates = new List<CodeBlockCandidate>();
        CollectCodeBlocks(document.Blocks, candidates);
        if (candidates.Count == 0)
        {
            return;
        }

        var enhancedAny = false;
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var candidate = candidates[i];
            var language = ResolveLanguage(fencedBlocks, i);
            var fencedBlock = fencedBlocks is not null && i < fencedBlocks.Count ? fencedBlocks[i] : null;
            var codeText = ResolveCodeText(candidate.Block, fencedBlock);
            if (string.IsNullOrWhiteSpace(codeText))
            {
                candidate.ParentBlocks.Remove(candidate.Block);
                continue;
            }

            var card = BuildCard(language, codeText);
            InsertBlockAt(candidate.ParentBlocks, candidate.Index, candidate.Block, card);
            enhancedAny = true;
        }

        if (enhancedAny)
        {
            document.Tag = CodeBlockCardTag;
        }
    }

    public static void ReapplyTheme(FlowDocument document)
    {
        ReapplyThemeBlocks(document.Blocks);
    }

    public static CodeBlockCardState? FindCardState(DependencyObject? element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is FrameworkElement { Tag: CodeBlockCardState state })
            {
                return state;
            }

            var parent = LogicalTreeHelper.GetParent(current);
            if (parent is null && current is Visual visual)
            {
                parent = VisualTreeHelper.GetParent(visual);
            }

            current = parent;
        }

        return null;
    }

    private sealed class CodeBlockCandidate
    {
        public required Block Block { get; init; }
        public required BlockCollection ParentBlocks { get; init; }
        public required int Index { get; init; }
    }

    private static void InsertBlockAt(
        BlockCollection parentBlocks,
        int index,
        Block existingBlock,
        Block replacementBlock)
    {
        parentBlocks.Remove(existingBlock);

        Block? anchor = null;
        var currentIndex = 0;
        foreach (Block block in parentBlocks)
        {
            if (currentIndex == index)
            {
                anchor = block;
                break;
            }

            currentIndex++;
        }

        if (anchor is not null)
        {
            parentBlocks.InsertBefore(anchor, replacementBlock);
            return;
        }

        parentBlocks.Add(replacementBlock);
    }

    private static void CollectCodeBlocks(BlockCollection blocks, List<CodeBlockCandidate> result)
    {
        var index = 0;
        foreach (Block block in blocks)
        {
            if (block is BlockUIContainer { Tag: CodeBlockCardTag })
            {
                continue;
            }

            var isCodeBlock = IsCodeBlock(block)
                || block is BlockUIContainer { Child: Border { Tag: "CodeBlock" } };

            if (isCodeBlock)
            {
                result.Add(new CodeBlockCandidate
                {
                    Block = block,
                    ParentBlocks = blocks,
                    Index = index,
                });
            }
            else
            {
                switch (block)
                {
                    case Section section:
                        CollectCodeBlocks(section.Blocks, result);
                        break;
                    case List list:
                        foreach (ListItem item in list.ListItems)
                        {
                            CollectCodeBlocks(item.Blocks, result);
                        }

                        break;
                    case Table table:
                        foreach (var rowGroup in table.RowGroups)
                        {
                            foreach (var row in rowGroup.Rows)
                            {
                                foreach (var cell in row.Cells)
                                {
                                    CollectCodeBlocks(cell.Blocks, result);
                                }
                            }
                        }

                        break;
                }
            }

            index++;
        }
    }

    private static bool IsCodeBlock(Block block) =>
        block.Tag as string == "CodeBlock";

    private static string ResolveLanguage(IReadOnlyList<FencedBlockInfo>? fencedBlocks, int codeBlockIndex)
    {
        if (fencedBlocks is null || codeBlockIndex >= fencedBlocks.Count)
        {
            return "代码";
        }

        var language = fencedBlocks[codeBlockIndex].Language;
        return string.IsNullOrWhiteSpace(language) ? "代码" : language;
    }

    private static string ResolveCodeText(Block block, FencedBlockInfo? fencedBlock)
    {
        var fromDocument = ExtractCodeTextFromBlock(block);
        var fromSource = fencedBlock?.Content ?? string.Empty;

        if (string.IsNullOrEmpty(fromDocument))
        {
            return fromSource.TrimEnd('\r', '\n');
        }

        if (string.IsNullOrEmpty(fromSource))
        {
            return fromDocument;
        }

        return fromSource.Length >= fromDocument.Length
            ? fromSource.TrimEnd('\r', '\n')
            : fromDocument;
    }

    private static string ExtractCodeTextFromBlock(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                return GetParagraphText(paragraph);
            case Section section:
            {
                var builder = new System.Text.StringBuilder();
                foreach (var child in section.Blocks)
                {
                    var childText = ExtractCodeTextFromBlock(child);
                    if (string.IsNullOrEmpty(childText))
                    {
                        continue;
                    }

                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(childText);
                }

                return builder.ToString().TrimEnd('\r', '\n');
            }
            case BlockUIContainer container:
                return container.Child is null
                    ? string.Empty
                    : ExtractUiText(container.Child).TrimEnd('\r', '\n');
            case List list:
            {
                var builder = new System.Text.StringBuilder();
                foreach (ListItem item in list.ListItems)
                {
                    foreach (var child in item.Blocks)
                    {
                        var childText = ExtractCodeTextFromBlock(child);
                        if (string.IsNullOrEmpty(childText))
                        {
                            continue;
                        }

                        if (builder.Length > 0)
                        {
                            builder.Append('\n');
                        }

                        builder.Append(childText);
                    }
                }

                return builder.ToString().TrimEnd('\r', '\n');
            }
            default:
                return string.Empty;
        }
    }

    private static string GetParagraphText(Paragraph paragraph)
    {
        if (paragraph.Inlines.Count == 0)
        {
            return string.Empty;
        }

        return new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd('\r', '\n');
    }

    private static string ExtractUiText(DependencyObject? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        switch (element)
        {
            case TextBlock textBlock:
                return textBlock.Text;
            case TextBox textBox:
                return textBox.Text;
            case Paragraph paragraph:
                return new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
            case Border border:
                return ExtractUiText(border.Child);
            case ScrollViewer scrollViewer:
                return ExtractUiText(scrollViewer.Content as DependencyObject);
            case ContentControl contentControl:
                return ExtractUiText(contentControl.Content as DependencyObject);
            case Panel panel:
            {
                var builder = new System.Text.StringBuilder();
                foreach (var child in panel.Children)
                {
                    if (child is not DependencyObject childObject)
                    {
                        continue;
                    }

                    var text = ExtractUiText(childObject);
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append('\n');
                        }

                        builder.Append(text);
                    }
                }

                return builder.ToString();
            }
            default:
            {
                if (element is Visual visualElement)
                {
                    var childCount = VisualTreeHelper.GetChildrenCount(visualElement);
                    if (childCount == 1)
                    {
                        return ExtractUiText(VisualTreeHelper.GetChild(visualElement, 0));
                    }
                }

                return string.Empty;
            }
        }
    }

    private static void ReapplyThemeBlocks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case BlockUIContainer { Tag: CodeBlockCardTag, Child: Border outerBorder }:
                    ApplyCardTheme(outerBorder);
                    break;
                case Section section:
                    ReapplyThemeBlocks(section.Blocks);
                    break;
                case List list:
                    foreach (ListItem item in list.ListItems)
                    {
                        ReapplyThemeBlocks(item.Blocks);
                    }

                    break;
                case Table table:
                    foreach (var rowGroup in table.RowGroups)
                    {
                        foreach (var row in rowGroup.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                ReapplyThemeBlocks(cell.Blocks);
                            }
                        }
                    }

                    break;
            }
        }
    }

    private static void ApplyCardTheme(Border outerBorder)
    {
        var codeBackground = ThemeBrushResolver.Get("Brush.CodeBackground");
        var codeForeground = ThemeBrushResolver.Get("Brush.CodeForeground");
        var codeBorder = ThemeBrushResolver.Get("Brush.CodeBorder");
        var headerBackground = ThemeBrushResolver.Get("Brush.Panel");
        var headerText = ThemeBrushResolver.Get("Brush.SubtleText");
        var copyButtonStyle = Application.Current?.TryFindResource("CodeBlockActionButtonStyle") as Style;

        outerBorder.Background = codeBackground;
        outerBorder.BorderBrush = codeBorder;

        if (outerBorder.Child is not Grid grid)
        {
            return;
        }

        foreach (var child in grid.Children)
        {
            switch (child)
            {
                case Border { Child: DockPanel headerDock } headerBorder:
                    headerBorder.Background = headerBackground;
                    foreach (var headerChild in headerDock.Children)
                    {
                        switch (headerChild)
                        {
                            case Button copyButton:
                                copyButton.Style = copyButtonStyle;
                                break;
                            case TextBlock languageLabel:
                                languageLabel.Foreground = headerText;
                                break;
                        }
                    }

                    break;
                case Border { Height: 1 } divider:
                    divider.Background = codeBorder;
                    break;
                case ScrollViewer bodyScroll:
                    bodyScroll.Background = codeBackground;
                    if (bodyScroll.Content is TextBox body)
                    {
                        body.Foreground = codeForeground;
                    }

                    break;
            }
        }
    }

    private static BlockUIContainer BuildCard(string language, string codeText)
    {
        var codeBackground = ThemeBrushResolver.Get("Brush.CodeBackground");
        var codeForeground = ThemeBrushResolver.Get("Brush.CodeForeground");
        var codeBorder = ThemeBrushResolver.Get("Brush.CodeBorder");
        var headerBackground = ThemeBrushResolver.Get("Brush.Panel");
        var headerText = ThemeBrushResolver.Get("Brush.SubtleText");
        var copyButtonStyle = Application.Current?.TryFindResource("CodeBlockActionButtonStyle") as Style;

        var cardState = new CodeBlockCardState { Text = codeText };

        var copyButton = new Button
        {
            Content = "复制",
            Style = copyButtonStyle,
        };
        copyButton.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(codeText))
            {
                Clipboard.SetText(codeText);
                if (Application.Current?.MainWindow?.DataContext is ViewModels.MainShellViewModel vm)
                {
                    vm.ShowCopyNotice("已复制");
                }
            }
        };

        var headerContent = new DockPanel { LastChildFill = true };

        DockPanel.SetDock(copyButton, Dock.Right);
        headerContent.Children.Add(copyButton);
        headerContent.Children.Add(new TextBlock
        {
            Text = language,
            FontSize = 12,
            Foreground = headerText,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var header = new Border
        {
            Background = headerBackground,
            Padding = new Thickness(16, 8, 16, 8),
            Child = headerContent,
        };

        var body = new TextBox
        {
            Text = codeText,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Consolas, Cascadia Code"),
            FontSize = 13,
            Foreground = codeForeground,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            AcceptsTab = true,
            Margin = new Thickness(16, 12, 16, 16),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false,
            Cursor = System.Windows.Input.Cursors.IBeam,
        };
        body.SelectionChanged += (_, _) => CodeBlockInteractionChanged?.Invoke(body, EventArgs.Empty);

        var bodyScroll = new ScrollViewer
        {
            Background = codeBackground,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 480,
            Content = body,
            Focusable = false,
        };

        var divider = new Border
        {
            Height = 1,
            Background = codeBorder,
        };

        var contentGrid = new Grid();
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(divider, 1);
        Grid.SetRow(bodyScroll, 2);
        contentGrid.Children.Add(header);
        contentGrid.Children.Add(divider);
        contentGrid.Children.Add(bodyScroll);

        var outerBorder = new Border
        {
            Background = codeBackground,
            BorderBrush = codeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 12, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = contentGrid,
            Tag = cardState,
        };

        return new BlockUIContainer(outerBorder)
        {
            Tag = CodeBlockCardTag,
            Margin = new Thickness(0),
        };
    }
}
