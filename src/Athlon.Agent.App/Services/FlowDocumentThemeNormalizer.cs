using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.App.Services;

public static class FlowDocumentThemeNormalizer
{
    public static void Normalize(
        FlowDocument document,
        ContextMenu? contextMenu,
        IReadOnlyList<FencedBlockInfo>? fencedBlocks = null,
        bool assistantTone = true)
    {
        var codeBackground = ThemeBrushResolver.Get("Brush.CodeBackground");
        var codeForeground = ThemeBrushResolver.Get("Brush.CodeForeground");
        var textBrush = assistantTone
            ? ThemeBrushResolver.Get("Brush.Text")
            : Brushes.White;
        var tableHeaderBackground = ThemeBrushResolver.Get("Brush.CodeBackgroundAlt");
        var tableBorder = ThemeBrushResolver.Get("Brush.TableBorder");
        NormalizeBlocks(document.Blocks, codeBackground, codeForeground, textBrush, tableHeaderBackground, tableBorder, contextMenu);
        FlowDocumentCodeBlockEnhancer.Enhance(document, fencedBlocks);
        FlowDocumentCodeBlockEnhancer.ReapplyTheme(document);
    }

    public static Brush? ResolveBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;

    private static void NormalizeBlocks(
        BlockCollection blocks,
        Brush codeBackground,
        Brush codeForeground,
        Brush textBrush,
        Brush tableHeaderBackground,
        Brush tableBorder,
        ContextMenu? contextMenu)
    {
        foreach (var block in blocks.ToArray())
        {
            NormalizeTextElementColors(block, codeBackground, codeForeground, textBrush);

            switch (block)
            {
                case Section section:
                    NormalizeBlocks(section.Blocks, codeBackground, codeForeground, textBrush, tableHeaderBackground, tableBorder, contextMenu);
                    break;
                case Paragraph paragraph:
                    NormalizeInlines(paragraph.Inlines, codeBackground, codeForeground, textBrush);
                    break;
                case List list:
                    foreach (ListItem item in list.ListItems)
                    {
                        NormalizeBlocks(item.Blocks, codeBackground, codeForeground, textBrush, tableHeaderBackground, tableBorder, contextMenu);
                    }
                    break;
                case Table table:
                    foreach (var rowGroup in table.RowGroups)
                    {
                        foreach (var row in rowGroup.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                ApplyTableCellTheme(cell, codeBackground, tableHeaderBackground, tableBorder, textBrush);
                                NormalizeBlocks(cell.Blocks, codeBackground, codeForeground, textBrush, tableHeaderBackground, tableBorder, contextMenu);
                            }
                        }
                    }
                    break;
                case BlockUIContainer container:
                    SoftenThematicBreak(container);
                    NormalizeElementColors(container.Child, codeBackground, codeForeground, contextMenu);
                    break;
            }
        }
    }

    private static void NormalizeInlines(InlineCollection inlines, Brush codeBackground, Brush codeForeground, Brush textBrush)
    {
        foreach (var inline in inlines)
        {
            NormalizeTextElementColors(inline, codeBackground, codeForeground, textBrush);

            if (inline is Span span)
            {
                NormalizeInlines(span.Inlines, codeBackground, codeForeground, textBrush);
            }
        }
    }

    private static void ApplyTableCellTheme(
        TableCell cell,
        Brush tableBackground,
        Brush tableHeaderBackground,
        Brush tableBorder,
        Brush textBrush)
    {
        var isHeader = cell.Tag as string == "TableHeader";
        cell.Background = isHeader ? tableHeaderBackground : tableBackground;
        cell.BorderBrush = tableBorder;

        if (NeedsThemeTextForeground(cell.Foreground))
        {
            cell.Foreground = textBrush;
        }
    }

    private static void NormalizeElementColors(DependencyObject? element, Brush codeBackground, Brush codeForeground, ContextMenu? contextMenu)
    {
        if (element is null)
        {
            return;
        }

        var insideCodeCard = IsInsideCodeBlockCard(element);

        switch (element)
        {
            case Control control when IsLightBrush(control.Background):
                control.Background = codeBackground;
                if (!insideCodeCard || control is not Button)
                {
                    NormalizeControlForeground(control, codeForeground);
                }

                control.ContextMenu = contextMenu;
                break;
            case Control control:
                control.ContextMenu = contextMenu;
                if (!insideCodeCard || control is not Button)
                {
                    NormalizeControlForeground(control, codeForeground);
                }

                break;
            case Border border when IsLightBrush(border.Background):
                border.Background = codeBackground;
                border.ContextMenu = contextMenu;
                break;
            case Border border:
                border.ContextMenu = contextMenu;
                break;
            case Panel panel when IsLightBrush(panel.Background):
                panel.Background = codeBackground;
                panel.ContextMenu = contextMenu;
                break;
            case Panel panel:
                panel.ContextMenu = contextMenu;
                break;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < childCount; i++)
        {
            NormalizeElementColors(VisualTreeHelper.GetChild(element, i), codeBackground, codeForeground, contextMenu);
        }
    }

    private static void NormalizeTextElementColors(
        TextElement element,
        Brush codeBackground,
        Brush codeForeground,
        Brush textBrush)
    {
        var tag = element.Tag as string;
        var isCode = tag is "CodeSpan" or "CodeBlock";

        if (IsLightBrush(element.Background) || (!isCode && IsDarkBrush(element.Background) && AppThemeManager.CurrentKind == AppThemeKind.Light))
        {
            element.Background = codeBackground;
        }

        if (isCode)
        {
            element.Foreground = codeForeground;
            return;
        }

        if (IsLowContrastBlueBrush(element.Foreground))
        {
            element.Foreground = ThemeBrushResolver.Get("Brush.CodeHighlightBlue");
            return;
        }

        if (NeedsThemeTextForeground(element.Foreground))
        {
            element.Foreground = textBrush;
        }
    }

    private static bool NeedsThemeTextForeground(Brush? brush)
    {
        if (brush is not SolidColorBrush solid)
        {
            return false;
        }

        var luminance = 0.299 * solid.Color.R + 0.587 * solid.Color.G + 0.114 * solid.Color.B;
        return AppThemeManager.CurrentKind == AppThemeKind.Dark
            ? luminance < 128
            : luminance > 180;
    }

    private static void NormalizeControlForeground(Control control, Brush codeForeground)
    {
        if (IsDarkBrush(control.Foreground))
        {
            control.Foreground = codeForeground;
        }
        else if (IsLowContrastBlueBrush(control.Foreground))
        {
            control.Foreground = ThemeBrushResolver.Get("Brush.CodeHighlightBlue");
        }
    }

    private static bool IsInsideCodeBlockCard(DependencyObject? element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is FrameworkElement { Tag: CodeBlockCardState })
            {
                return true;
            }

            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsLightBrush(Brush? brush) =>
        brush is SolidColorBrush solid
        && solid.Color.R >= 220
        && solid.Color.G >= 220
        && solid.Color.B >= 220;

    private static bool IsDarkBrush(Brush? brush) =>
        brush is SolidColorBrush solid
        && solid.Color.R <= 80
        && solid.Color.G <= 80
        && solid.Color.B <= 80;

    private static bool IsLowContrastBlueBrush(Brush? brush) =>
        brush is SolidColorBrush solid
        && solid.Color.B >= 120
        && solid.Color.R <= 80
        && solid.Color.G <= 120;

    private static void SoftenThematicBreak(BlockUIContainer container)
    {
        if (container.Tag is not string tag || !tag.StartsWith("Rule", StringComparison.Ordinal))
        {
            return;
        }

        var borderBrush = ThemeBrushResolver.Get("Brush.Border");
        container.Child = new Border
        {
            Height = 1,
            Background = borderBrush,
            Opacity = 0.6,
            Margin = new Thickness(0, 12, 0, 12),
        };
        container.Tag = "RuleNormalized";
    }
}
