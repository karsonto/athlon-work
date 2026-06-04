using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Windows;
using MdXaml;

namespace Athlon.Agent.App.Controls;

/// <summary>
/// WPF-native markdown (MdXaml) inside a clipped ScrollViewer.
/// Avoids WebView2 HWND bleed-through over the composer.
/// </summary>
public partial class MarkdownMessageView : UserControl
{
    /// <summary>Raised when markdown text selection changes (used to pause chat auto-scroll).</summary>
    public static event EventHandler? ContentInteractionChanged;

    private ContextMenu? _contextMenu;
    private MenuItem? _previewHtmlMenuItem;
    private MenuItem? _previewMermaidMenuItem;
    private bool _documentHooked;
    private TextSelection? _textSelection;

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
            ? FlowDocumentThemeNormalizer.ResolveBrush("Brush.Text") ?? Brushes.White
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

        var inlineCodeBackground = FlowDocumentThemeNormalizer.ResolveBrush("Brush.CodeBackgroundAlt")
            ?? new SolidColorBrush(Color.FromRgb(39, 39, 42));
        var codeBlockBackground = FlowDocumentThemeNormalizer.ResolveBrush("Brush.CodeBackground")
            ?? new SolidColorBrush(Color.FromRgb(32, 32, 35));
        var codeForeground = FlowDocumentThemeNormalizer.ResolveBrush("Brush.CodeForeground")
            ?? new SolidColorBrush(Color.FromRgb(241, 245, 249));
        var codeBorder = FlowDocumentThemeNormalizer.ResolveBrush("Brush.CodeBorder")
            ?? new SolidColorBrush(Color.FromRgb(30, 41, 59));
        var tableBackground = codeBlockBackground;
        var tableHeaderBackground = inlineCodeBackground;
        var tableBorder = FlowDocumentThemeNormalizer.ResolveBrush("Brush.TableBorder")
            ?? new SolidColorBrush(Color.FromRgb(82, 82, 91));

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
        AttachSelectionHandler();
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

        DetachSelectionHandler();
        MarkdownViewer.Markdown = string.Empty;
        MarkdownViewer.Document = null;
    }

    private void AttachMarkdownContextMenu()
    {
        if (_contextMenu is null)
        {
            return;
        }

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

    private void OnMarkdownDocumentChanged(object? sender, EventArgs e)
    {
        ApplyFlowDocumentContextMenu();
        AttachSelectionHandler();
    }

    private void ApplyFlowDocumentContextMenu()
    {
        if (MarkdownViewer.Document is FlowDocument document)
        {
            document.ContextMenu = _contextMenu;
            FlowDocumentThemeNormalizer.Normalize(document, _contextMenu);
        }
    }

    private void AttachSelectionHandler()
    {
        DetachSelectionHandler();
        if (MarkdownViewer.Document is null)
        {
            return;
        }

        if (TrySubscribeToSelection())
        {
            return;
        }

        // Document is set before FlowDocumentScrollViewer.Selection is initialized.
        MarkdownViewer.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, DeferredAttachSelectionHandler);
    }

    private void DeferredAttachSelectionHandler()
    {
        if (MarkdownViewer.Document is null || _textSelection is not null)
        {
            return;
        }

        TrySubscribeToSelection();
    }

    private bool TrySubscribeToSelection()
    {
        _textSelection = MarkdownViewer.Selection;
        if (_textSelection is null)
        {
            return false;
        }

        _textSelection.Changed += OnTextSelectionChanged;
        return true;
    }

    private void DetachSelectionHandler()
    {
        if (_textSelection is null)
        {
            return;
        }

        _textSelection.Changed -= OnTextSelectionChanged;
        _textSelection = null;
    }

    private void OnTextSelectionChanged(object? sender, EventArgs e) =>
        ContentInteractionChanged?.Invoke(null, EventArgs.Empty);

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            Style = Application.Current.FindResource("MarkdownContextMenuStyle") as Style,
        };
        menu.Opened += (_, _) => UpdatePreviewMenuVisibility();

        _previewHtmlMenuItem = new MenuItem
        {
            Header = "预览 HTML",
            Style = Application.Current.FindResource("MarkdownContextMenuItemStyle") as Style,
            Visibility = Visibility.Collapsed,
        };
        _previewHtmlMenuItem.Click += (_, _) =>
        {
            if (HtmlContentDetector.LooksLikeHtml(Markdown))
            {
                HtmlPreviewWindow.Show(Markdown, Window.GetWindow(this));
            }
        };
        menu.Items.Add(_previewHtmlMenuItem);

        _previewMermaidMenuItem = new MenuItem
        {
            Header = "查看 Mermaid 图表",
            Style = Application.Current.FindResource("MarkdownContextMenuItemStyle") as Style,
            Visibility = Visibility.Collapsed,
        };
        _previewMermaidMenuItem.Click += (_, _) =>
            MermaidPreviewWindow.Show(Markdown, Window.GetWindow(this));
        menu.Items.Add(_previewMermaidMenuItem);

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

    private void UpdatePreviewMenuVisibility()
    {
        if (_previewHtmlMenuItem is not null)
        {
            _previewHtmlMenuItem.Visibility = HtmlContentDetector.LooksLikeHtml(Markdown)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_previewMermaidMenuItem is not null)
        {
            _previewMermaidMenuItem.Visibility = MermaidMarkdownExtractor.ContainsMermaid(Markdown)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}
