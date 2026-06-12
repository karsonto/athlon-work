using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Athlon.Agent.App.Services;

public static class FlowDocumentThemeNormalizer
{
    public static void Normalize(
        FlowDocument document,
        ContextMenu? contextMenu,
        IReadOnlyList<FencedBlockInfo>? fencedBlocks = null)
    {
        var codeBackground = ThemeBrushResolver.Get("Brush.CodeBackground");
        var codeForeground = ThemeBrushResolver.Get("Brush.CodeForeground");
        NormalizeBlocks(document.Blocks, codeBackground, codeForeground, contextMenu);
        FlowDocumentCodeBlockEnhancer.Enhance(document, fencedBlocks);
    }

    public static Brush? ResolveBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;

    private static void NormalizeBlocks(BlockCollection blocks, Brush codeBackground, Brush codeForeground, ContextMenu? contextMenu)
    {
        foreach (var block in blocks.ToArray())
        {
            NormalizeTextElementColors(block, codeBackground, codeForeground);

            switch (block)
            {
                case Section section:
                    NormalizeBlocks(section.Blocks, codeBackground, codeForeground, contextMenu);
                    break;
                case Paragraph paragraph:
                    NormalizeInlines(paragraph.Inlines, codeBackground, codeForeground);
                    break;
                case List list:
                    foreach (ListItem item in list.ListItems)
                    {
                        NormalizeBlocks(item.Blocks, codeBackground, codeForeground, contextMenu);
                    }
                    break;
                case Table table:
                    foreach (var rowGroup in table.RowGroups)
                    {
                        foreach (var row in rowGroup.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                NormalizeTextElementColors(cell, codeBackground, codeForeground);
                                NormalizeBlocks(cell.Blocks, codeBackground, codeForeground, contextMenu);
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

    private static void NormalizeInlines(InlineCollection inlines, Brush codeBackground, Brush codeForeground)
    {
        foreach (var inline in inlines)
        {
            NormalizeTextElementColors(inline, codeBackground, codeForeground);

            if (inline is Span span)
            {
                NormalizeInlines(span.Inlines, codeBackground, codeForeground);
            }
        }
    }

    private static void NormalizeElementColors(DependencyObject? element, Brush codeBackground, Brush codeForeground, ContextMenu? contextMenu)
    {
        if (element is null)
        {
            return;
        }

        switch (element)
        {
            case Control control when IsLightBrush(control.Background):
                control.Background = codeBackground;
                NormalizeControlForeground(control, codeForeground);
                control.ContextMenu = contextMenu;
                break;
            case Control control:
                control.ContextMenu = contextMenu;
                NormalizeControlForeground(control, codeForeground);
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

    private static void NormalizeTextElementColors(TextElement element, Brush codeBackground, Brush codeForeground)
    {
        if (IsLightBrush(element.Background))
        {
            element.Background = codeBackground;
        }

        if (IsDarkBrush(element.Foreground))
        {
            element.Foreground = codeForeground;
        }
        else if (IsLowContrastBlueBrush(element.Foreground))
        {
            element.Foreground = ThemeBrushResolver.Get("Brush.CodeHighlightBlue");
        }
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
