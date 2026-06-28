using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.Windows;

namespace Athlon.Agent.App.Controls;

/// <summary>
/// WPF-native markdown (MdXaml) inside a clipped ScrollViewer.
/// Avoids WebView2 HWND bleed-through over the composer.
/// </summary>
public partial class MarkdownMessageView : UserControl
{
    /// <summary>Bubbles when markdown text selection changes (used to pause chat auto-scroll).</summary>
    public static readonly RoutedEvent ContentInteractionChangedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(ContentInteractionChanged),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(MarkdownMessageView));

    public event RoutedEventHandler ContentInteractionChanged
    {
        add => AddHandler(ContentInteractionChangedEvent, value);
        remove => RemoveHandler(ContentInteractionChangedEvent, value);
    }

    private ContextMenu? _contextMenu;
    private MenuItem? _previewHtmlMenuItem;
    private MenuItem? _previewMermaidMenuItem;
    private MenuItem? _copyCodeBlockMenuItem;
    private bool _documentHooked;
    private TextSelection? _textSelection;
    private IReadOnlyList<FencedBlockInfo> _fencedBlocks = Array.Empty<FencedBlockInfo>();

    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(
            nameof(Markdown),
            typeof(string),
            typeof(MarkdownMessageView),
            new PropertyMetadata(string.Empty, OnMarkdownChanged));

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
            new PropertyMetadata(480d, OnScrollLayoutChanged));

    public static readonly DependencyProperty FillViewportProperty =
        DependencyProperty.Register(
            nameof(FillViewport),
            typeof(bool),
            typeof(MarkdownMessageView),
            new PropertyMetadata(false, OnScrollLayoutChanged));

    public MarkdownMessageView()
    {
        InitializeComponent();
        _contextMenu = BuildContextMenu();
        ContextMenu = _contextMenu;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        FlowDocumentCodeBlockEnhancer.CodeBlockInteractionChanged += OnCodeBlockInteractionChanged;
        AddHandler(
            FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(OnMarkdownRequestBringIntoView),
            handledEventsToo: true);
    }

    /// <summary>
    /// Selection and caret moves inside markdown raise RequestBringIntoView, which otherwise
    /// bubbles to the chat ListBox ScrollViewer and makes the right scrollbar jump.
    /// Inner HostScroll / code-block scrollers still handle the event on the way up.
    /// </summary>
    private void OnMarkdownRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (e.Handled || sender is not MarkdownMessageView markdownView)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source
            && IsDescendantOf(markdownView, source))
        {
            e.Handled = true;
        }
    }

    private static bool IsDescendantOf(DependencyObject ancestor, DependencyObject? node)
    {
        var current = node;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current is FrameworkElement or Visual
                ? LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current)
                : null;
        }

        return false;
    }

    private void OnCodeBlockInteractionChanged(object? sender, EventArgs e) =>
        RaiseContentInteractionChanged();

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

    public bool FillViewport
    {
        get => (bool)GetValue(FillViewportProperty);
        set => SetValue(FillViewportProperty, value);
    }

    private static void OnAssistantToneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownMessageView view)
        {
            view.ApplyTheme();
        }
    }

    private static void OnScrollLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownMessageView view)
        {
            view.ApplyScrollLayout();
        }
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownMessageView view)
        {
            if (!view.IsLoaded)
            {
                return;
            }

            view.RefreshDisplayMarkdown();
        }
    }

    private void RefreshDisplayMarkdown(bool forceRebuild = false)
    {
        var source = Markdown ?? string.Empty;
        _fencedBlocks = MarkdownDisplayNormalizer.ExtractFencedBlocks(source);
        var display = MarkdownDisplayNormalizer.NormalizeForDisplay(source);

        if (forceRebuild)
        {
            MarkdownViewer.Markdown = string.Empty;
        }

        MarkdownViewer.Markdown = display;
    }

    private void ApplyScrollLayout()
    {
        if (FillViewport)
        {
            VerticalAlignment = VerticalAlignment.Stretch;
            HostScroll.VerticalAlignment = VerticalAlignment.Stretch;
            HostScroll.ClearValue(FrameworkElement.MaxHeightProperty);
            HostScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            return;
        }

        VerticalAlignment = VerticalAlignment.Top;
        HostScroll.VerticalAlignment = VerticalAlignment.Top;

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
        ApplyScrollLayout();

        var textBrush = ChatMessageToneColors.GetMessageTextBrush(AssistantTone);
        MarkdownViewer.Foreground = textBrush;
        MarkdownViewer.MarkdownStyle = FlowDocumentMarkdownThemeFactory.CreateDocumentStyle(textBrush);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged += OnAppThemeChanged;
        ApplyTheme();
        RefreshDisplayMarkdown();
        AttachMarkdownContextMenu();
        HookMarkdownDocumentChanges();
        AttachSelectionHandler();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        FlowDocumentCodeBlockEnhancer.CodeBlockInteractionChanged -= OnCodeBlockInteractionChanged;

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

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
        RefreshDisplayMarkdown(forceRebuild: true);
    }

    private void ApplyFlowDocumentContextMenu()
    {
        if (MarkdownViewer.Document is FlowDocument document)
        {
            document.ContextMenu = _contextMenu;
            document.Tag = null;
            FlowDocumentThemeNormalizer.Normalize(document, _contextMenu, _fencedBlocks, AssistantTone);
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
        RaiseContentInteractionChanged();

    private void RaiseContentInteractionChanged() =>
        RaiseEvent(new RoutedEventArgs(ContentInteractionChangedEvent));

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            Style = Application.Current.FindResource("MarkdownContextMenuStyle") as Style,
        };
        menu.Opened += (_, _) =>
        {
            UpdatePreviewMenuVisibility();
            UpdateCopyCodeBlockMenuVisibility();
        };

        _previewHtmlMenuItem = new MenuItem
        {
            Header = Strings.Get("Markdown_PreviewHtml"),
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
            Header = Strings.Get("Markdown_PreviewMermaid"),
            Style = Application.Current.FindResource("MarkdownContextMenuItemStyle") as Style,
            Visibility = Visibility.Collapsed,
        };
        _previewMermaidMenuItem.Click += (_, _) =>
            MermaidPreviewWindow.Show(Markdown, Window.GetWindow(this));
        menu.Items.Add(_previewMermaidMenuItem);

        var copyItem = new MenuItem
        {
            Header = Strings.Get("Markdown_CopyContent"),
            Style = Application.Current.FindResource("MarkdownContextMenuItemStyle") as Style,
        };
        copyItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(Markdown))
            {
                Clipboard.SetText(Markdown);
                if (Application.Current.MainWindow?.DataContext is ViewModels.MainShellViewModel vm)
                {
                    vm.ShowCopyNotice(Strings.Get("Markdown_ContentCopied"));
                }
            }
        };
        menu.Items.Add(copyItem);

        _copyCodeBlockMenuItem = new MenuItem
        {
            Header = Strings.Get("Markdown_CopyCode"),
            Style = Application.Current.FindResource("MarkdownContextMenuItemStyle") as Style,
            Visibility = Visibility.Collapsed,
        };
        _copyCodeBlockMenuItem.Click += (_, _) =>
        {
            var cardState = FindContextCodeBlockState();
            if (cardState is null || string.IsNullOrEmpty(cardState.Text))
            {
                return;
            }

            Clipboard.SetText(cardState.Text);
            if (Application.Current.MainWindow?.DataContext is ViewModels.MainShellViewModel vm)
            {
                vm.ShowCopyNotice(Strings.Get("FlowDoc_CopyNotice"));
            }
        };
        menu.Items.Add(_copyCodeBlockMenuItem);

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

    private void UpdateCopyCodeBlockMenuVisibility()
    {
        if (_copyCodeBlockMenuItem is null || _contextMenu is null)
        {
            return;
        }

        _copyCodeBlockMenuItem.Visibility = FindContextCodeBlockState() is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private CodeBlockCardState? FindContextCodeBlockState()
    {
        if (_contextMenu?.PlacementTarget is DependencyObject placementTarget)
        {
            var fromPlacement = FlowDocumentCodeBlockEnhancer.FindCardState(placementTarget);
            if (fromPlacement is not null)
            {
                return fromPlacement;
            }
        }

        if (MarkdownViewer.Selection is not { IsEmpty: false } selection)
        {
            return null;
        }

        return FlowDocumentCodeBlockEnhancer.FindCardState(selection.Start.Parent as DependencyObject);
    }
}
