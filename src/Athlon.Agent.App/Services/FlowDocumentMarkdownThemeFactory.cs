using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Athlon.Agent.App.Themes;
using MdXaml;

namespace Athlon.Agent.App.Services;

/// <summary>Builds MdXaml FlowDocument styles from the active theme palette.</summary>
public static class FlowDocumentMarkdownThemeFactory
{
    public static Style CreateDocumentStyle(bool assistantTone)
    {
        var textBrush = ChatMessageToneColors.GetMessageTextBrush(assistantTone);
        return CreateDocumentStyle(textBrush);
    }

    public static Style CreateDocumentStyle(Brush textBrush)
    {
        var style = new Style(typeof(FlowDocument), MarkdownStyle.Standard)
        {
            Setters =
            {
                new Setter(TextElement.ForegroundProperty, textBrush),
                new Setter(FlowDocument.PagePaddingProperty, new Thickness(0)),
                new Setter(FlowDocument.BackgroundProperty, Brushes.Transparent),
            }
        };

        var inlineCodeBackground = ThemeBrushResolver.Get("Brush.CodeBackgroundAlt");
        var codeBlockBackground = ThemeBrushResolver.Get("Brush.CodeBackground");
        var codeForeground = ThemeBrushResolver.Get("Brush.CodeForeground");
        var codeBorder = ThemeBrushResolver.Get("Brush.CodeBorder");
        var tableBackground = codeBlockBackground;
        var tableHeaderBackground = inlineCodeBackground;
        var tableBorder = ThemeBrushResolver.Get("Brush.TableBorder");

        var paragraphStyle = new Style(typeof(Paragraph))
        {
            Setters = { new Setter(TextElement.ForegroundProperty, textBrush) }
        };
        AddTagStyle(paragraphStyle, "CodeSpan", inlineCodeBackground, codeForeground);
        AddTagStyle(paragraphStyle, "CodeBlock", codeBlockBackground, codeForeground, new Thickness(12), new Thickness(0, 12, 0, 12));
        style.Resources.Add(typeof(Paragraph), paragraphStyle);

        var borderStyle = new Style(typeof(Border));
        AddTagStyle(borderStyle, "CodeBlock", codeBlockBackground, codeForeground, new Thickness(12), new Thickness(0, 12, 0, 12));
        borderStyle.Triggers.Add(CreateTagTrigger(
            "CodeBlock",
            new Setter(Border.BorderBrushProperty, codeBorder),
            new Setter(Border.BorderThicknessProperty, new Thickness(1)),
            new Setter(Border.CornerRadiusProperty, new CornerRadius(12))));
        style.Resources.Add(typeof(Border), borderStyle);

        var runStyle = new Style(typeof(Run));
        AddTagStyle(runStyle, "CodeSpan", inlineCodeBackground, codeForeground);
        style.Resources.Add(typeof(Run), runStyle);

        var tableStyle = new Style(typeof(Table))
        {
            Setters =
            {
                new Setter(Table.CellSpacingProperty, 0d),
                new Setter(Block.MarginProperty, new Thickness(0, 8, 0, 8)),
            }
        };
        style.Resources.Add(typeof(Table), tableStyle);

        var tableCellStyle = new Style(typeof(TableCell))
        {
            Setters =
            {
                new Setter(TableCell.BackgroundProperty, tableBackground),
                new Setter(TableCell.BorderBrushProperty, tableBorder),
                new Setter(TableCell.BorderThicknessProperty, new Thickness(1)),
                new Setter(TableCell.PaddingProperty, new Thickness(8, 5, 8, 5)),
                new Setter(TextElement.ForegroundProperty, textBrush),
            }
        };
        tableCellStyle.Triggers.Add(CreateTagTrigger(
            "TableHeader",
            new Setter(TableCell.BackgroundProperty, tableHeaderBackground),
            new Setter(TextElement.FontWeightProperty, FontWeights.SemiBold)));
        style.Resources.Add(typeof(TableCell), tableCellStyle);

        AddSeparatorStyle(style);

        return style;
    }

    private static void AddSeparatorStyle(Style documentStyle)
    {
        var borderBrush = ThemeBrushResolver.Get("Brush.Border");

        var separatorStyle = new Style(typeof(Separator))
        {
            Setters =
            {
                new Setter(Control.BackgroundProperty, borderBrush),
                new Setter(Control.HeightProperty, 1d),
                new Setter(Control.OpacityProperty, 0.6d),
                new Setter(FrameworkElement.MarginProperty, new Thickness(0)),
            },
        };

        documentStyle.Resources.Add(typeof(Separator), separatorStyle);
    }

    private static void AddTagStyle(
        Style target,
        string tag,
        Brush background,
        Brush? foreground = null,
        Thickness? padding = null,
        Thickness? margin = null)
    {
        var setters = new List<Setter>
        {
            new(BackgroundPropertyFor(target.TargetType), background),
        };

        if (foreground is not null && target.TargetType != typeof(Border))
        {
            setters.Add(new Setter(TextElement.ForegroundProperty, foreground));
        }

        if (padding is not null)
        {
            if (target.TargetType == typeof(Border))
            {
                setters.Add(new Setter(Border.PaddingProperty, padding));
            }
            else if (typeof(Block).IsAssignableFrom(target.TargetType))
            {
                setters.Add(new Setter(Block.PaddingProperty, padding));
            }
        }

        if (margin is not null)
        {
            if (target.TargetType == typeof(Border))
            {
                setters.Add(new Setter(Border.MarginProperty, margin));
            }
            else if (typeof(Block).IsAssignableFrom(target.TargetType))
            {
                setters.Add(new Setter(Block.MarginProperty, margin));
            }
        }

        target.Triggers.Add(CreateTagTrigger(tag, setters.ToArray()));
    }

    private static DependencyProperty BackgroundPropertyFor(Type targetType) =>
        targetType == typeof(Border) ? Border.BackgroundProperty : TextElement.BackgroundProperty;

    private static Trigger CreateTagTrigger(string tag, params Setter[] setters)
    {
        var trigger = new Trigger
        {
            Property = FrameworkElement.TagProperty,
            Value = tag,
        };

        foreach (var setter in setters)
        {
            trigger.Setters.Add(setter);
        }

        return trigger;
    }
}
