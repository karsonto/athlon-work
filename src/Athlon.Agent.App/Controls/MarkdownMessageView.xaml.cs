using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MdXaml;

namespace Athlon.Agent.App.Controls;

/// <summary>
/// WPF-native markdown (MdXaml) inside a clipped ScrollViewer.
/// Avoids WebView2 HWND bleed-through over the composer.
/// </summary>
public partial class MarkdownMessageView : UserControl
{
    private ContextMenu? _contextMenu;
    private bool _documentHooked;

    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(
            nameof(Markdown),
            typeof(string),
            typeof(MarkdownMessageView),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty AssistantToneProperty =
        DependencyProperty.Register(
            nameof(AssistantTone),
            typeof(bool),
            typeof(MarkdownMessageView),
            new PropertyMetadata(true, OnAssistantToneChanged));

    public static readonly DependencyProperty MaxContentHeightProperty =
        DependencyProperty.Register(
            nameof(MaxContentHeight),
            typeof(double),
            typeof(MarkdownMessageView),
            new PropertyMetadata(480d, OnMaxContentHeightChanged));

    public MarkdownMessageView()
    {
        InitializeComponent();
        _contextMenu = BuildContextMenu();
        ContextMenu = _contextMenu;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool AssistantTone
    {
        get => (bool)GetValue(AssistantToneProperty);
        set => SetValue(AssistantToneProperty, value);
    }

    public double MaxContentHeight
    {
        get => (double)GetValue(MaxContentHeightProperty);
        set => SetValue(MaxContentHeightProperty, value);
    }

    private static void OnAssistantToneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownMessageView view)
        {
            view.ApplyTheme();
        }
    }

    private static void OnMaxContentHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownMessageView view)
        {
            view.ApplyMaxHeight();
        }
    }

    private void ApplyMaxHeight()
    {
        if (MaxContentHeight <= 0)
        {
            HostScroll.ClearValue(FrameworkElement.MaxHeightProperty);
            HostScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            return;
        }

        HostScroll.MaxHeight = MaxContentHeight;
        HostScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    private void ApplyTheme()
    {
        ApplyMaxHeight();

        var textBrush = AssistantTone
            ? Application.Current.FindResource("Brush.Text") as Brush ?? Brushes.White
            : new SolidColorBrush(Color.FromRgb(239, 246, 255));

        MarkdownViewer.Foreground = textBrush;
        MarkdownViewer.MarkdownStyle = BuildDarkMarkdownStyle(textBrush);
    }

    private static Style BuildDarkMarkdownStyle(Brush textBrush)
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

        var inlineCodeBackground = new SolidColorBrush(Color.FromRgb(39, 39, 42));
        var codeBlockBackground = new SolidColorBrush(Color.FromRgb(2, 6, 23));
        var codeForeground = new SolidColorBrush(Color.FromRgb(241, 245, 249));
        var codeBorder = new SolidColorBrush(Color.FromRgb(30, 41, 59));
        var tableBackground = new SolidColorBrush(Color.FromRgb(32, 32, 35));
        var tableHeaderBackground = new SolidColorBrush(Color.FromRgb(39, 39, 42));
        var tableBorder = new SolidColorBrush(Color.FromRgb(82, 82, 91));

        var paragraphStyle = new Style(typeof(Paragraph))
        {
            Setters = { new Setter(TextElement.ForegroundProperty, textBrush) }
        };
        AddTagStyle(paragraphStyle, "CodeSpan", inlineCodeBackground, codeForeground);
        AddTagStyle(paragraphStyle, "CodeBlock", codeBlockBackground, codeForeground, new Thickness(12), new Thickness(0, 8, 0, 8));
        style.Resources.Add(typeof(Paragraph), paragraphStyle);

        var borderStyle = new Style(typeof(Border));
        AddTagStyle(borderStyle, "CodeBlock", codeBlockBackground, codeForeground, new Thickness(12), new Thickness(0, 8, 0, 8));
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

        return style;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme();
        AttachMarkdownContextMenu();
        HookMarkdownDocumentChanges();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_documentHooked)
        {
            DependencyPropertyDescriptor
                .FromProperty(FlowDocumentScrollViewer.DocumentProperty, typeof(FlowDocumentScrollViewer))
                .RemoveValueChanged(MarkdownViewer, OnMarkdownDocumentChanged);
            _documentHooked = false;
        }
    }

    private void AttachMarkdownContextMenu()
    {
        if (_contextMenu is null)
        {
            return;
        }

        // MdXaml/FlowDocument 会在文档重建后恢复默认菜单，此处统一替换为应用菜单。
        MarkdownViewer.ContextMenu = _contextMenu;
        ApplyFlowDocumentContextMenu();
    }

    private void HookMarkdownDocumentChanges()
    {
        if (_documentHooked)
        {
            return;
        }

        DependencyPropertyDescriptor
            .FromProperty(FlowDocumentScrollViewer.DocumentProperty, typeof(FlowDocumentScrollViewer))
            .AddValueChanged(MarkdownViewer, OnMarkdownDocumentChanged);
        _documentHooked = true;
        OnMarkdownDocumentChanged(MarkdownViewer, EventArgs.Empty);
    }

    private void OnMarkdownDocumentChanged(object? sender, EventArgs e) => ApplyFlowDocumentContextMenu();

    private void ApplyFlowDocumentContextMenu()
    {
        if (MarkdownViewer.Document is FlowDocument document)
        {
            document.ContextMenu = _contextMenu;
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            Style = Application.Current.FindResource("MarkdownContextMenuStyle") as Style,
        };
        var copyItem = new MenuItem
        {
            Header = "复制内容",
            Style = Application.Current.FindResource("MarkdownContextMenuItemStyle") as Style,
        };
        copyItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(Markdown))
            {
                Clipboard.SetText(Markdown);
                if (Application.Current.MainWindow?.DataContext is ViewModels.MainWindowViewModel vm)
                {
                    vm.ShowCopyNotice("内容已复制到剪贴板");
                }
            }
        };
        menu.Items.Add(copyItem);
        return menu;
    }
}
