using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Athlon.Agent.App.Controls;

public partial class FileEditorView : UserControl
{
    private FileEditorViewModel? _editor;
    private EditorDocumentViewModel? _loadedDocument;
    private bool _suppressEditorChange;
    private bool _htmlPreviewReady;
    private string? _lastHtmlPreviewContent;
    private readonly DispatcherTimer _previewRefreshTimer;

    public FileEditorView()
    {
        InitializeComponent();
        _previewRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _previewRefreshTimer.Tick += OnPreviewRefreshTimerTick;
        ApplyEditorChrome();
        CodeEditor.TextChanged += OnCodeEditorTextChanged;
        DataContextChanged += (_, _) => AttachEditor(DataContext as FileEditorViewModel);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => AppThemeManager.ThemeChanged += OnThemeChanged;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged -= OnThemeChanged;
        _previewRefreshTimer.Stop();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyEditorChrome();
        if (_loadedDocument is null || _loadedDocument.ShowPreview)
        {
            if (_loadedDocument is { ShowPreview: true, IsHtmlFile: true })
            {
                _lastHtmlPreviewContent = null;
                RefreshPreviewSurface(immediate: true);
            }

            return;
        }

        CodeEditor.SyntaxHighlighting = EditorSyntaxHighlighting.Resolve(_loadedDocument.FilePath);
        CodeEditor.TextArea.TextView.Redraw();
    }

    private void ApplyEditorChrome()
    {
        var editorBackground = new SolidColorBrush(EditorSyntaxHighlighting.EditorBackground);
        EditorRoot.Background = editorBackground;
        EditorSurface.Background = editorBackground;
        CodeEditor.Background = editorBackground;
        CodeEditor.Foreground = new SolidColorBrush(EditorSyntaxHighlighting.DefaultText);
        CodeEditor.LineNumbersForeground = new SolidColorBrush(EditorSyntaxHighlighting.LineNumber);
        CodeEditor.TextArea.Caret.CaretBrush = new SolidColorBrush(EditorSyntaxHighlighting.DefaultText);
        CodeEditor.TextArea.SelectionBrush = new SolidColorBrush(EditorSyntaxHighlighting.SelectionBackground);
        CodeEditor.TextArea.SelectionForeground = new SolidColorBrush(EditorSyntaxHighlighting.SelectionForeground);
        CodeEditor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(EditorSyntaxHighlighting.Link);
        CodeEditor.TextArea.TextView.CurrentLineBackground =
            new SolidColorBrush(EditorSyntaxHighlighting.CurrentLineBackground);
    }

    private void AttachEditor(FileEditorViewModel? editor)
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorPropertyChanged;
        }

        _editor = editor;
        if (_editor is not null)
        {
            _editor.PropertyChanged += OnEditorPropertyChanged;
        }

        LoadActiveDocument();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileEditorViewModel.ActiveDocument))
        {
            SyncEditorToActiveDocument();
            LoadActiveDocument();
        }
    }

    private void SyncEditorToActiveDocument()
    {
        if (_suppressEditorChange || _loadedDocument is null || _loadedDocument.ShowPreview)
        {
            return;
        }

        var text = CodeEditor.Document.Text;
        if (!string.Equals(_loadedDocument.Content, text, StringComparison.Ordinal))
        {
            _loadedDocument.Content = text;
        }
    }

    private void LoadActiveDocument()
    {
        var document = _editor?.ActiveDocument;
        if (ReferenceEquals(document, _loadedDocument))
        {
            ApplyReadOnlyState();
            UpdateEditorSurfaceMode();
            RefreshPreviewSurface();
            return;
        }

        if (_loadedDocument is not null)
        {
            _loadedDocument.PropertyChanged -= OnActiveDocumentPropertyChanged;
        }

        _loadedDocument = document;
        if (_loadedDocument is not null)
        {
            _loadedDocument.PropertyChanged += OnActiveDocumentPropertyChanged;
        }

        _lastHtmlPreviewContent = null;
        _suppressEditorChange = true;
        try
        {
            if (document is null)
            {
                CodeEditor.Document.Text = string.Empty;
                CodeEditor.IsReadOnly = true;
                CodeEditor.SyntaxHighlighting = null;
                MarkdownPreview.Markdown = string.Empty;
                ClearHtmlPreview();
                UpdateEditorSurfaceMode();
                return;
            }

            CodeEditor.Document.Text = document.Content;
            CodeEditor.SyntaxHighlighting = EditorSyntaxHighlighting.Resolve(document.FilePath);
            CodeEditor.TextArea.TextView.Redraw();
            CodeEditor.ScrollToHome();
            ApplyReadOnlyState();
            UpdateEditorSurfaceMode();
            RefreshPreviewSurface(immediate: true);
        }
        finally
        {
            _suppressEditorChange = false;
        }
    }

    private void OnActiveDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorDocumentViewModel.IsReadOnly))
        {
            ApplyReadOnlyState();
            return;
        }

        if (e.PropertyName == nameof(EditorDocumentViewModel.ViewMode)
            || e.PropertyName == nameof(EditorDocumentViewModel.ShowPreview))
        {
            if (_loadedDocument?.ShowPreview == true)
            {
                SyncEditorToActiveDocumentBeforePreview();
            }
            else if (_loadedDocument is not null)
            {
                SyncDocumentContentToCodeEditor();
            }

            UpdateEditorSurfaceMode();
            RefreshPreviewSurface(immediate: true);
            return;
        }

        if (e.PropertyName == nameof(EditorDocumentViewModel.Content))
        {
            if (_loadedDocument?.ShowPreview == true)
            {
                SyncDocumentContentToCodeEditor();
                RefreshPreviewSurface();
            }
            else if (!_suppressEditorChange
                     && _loadedDocument is not null
                     && !string.Equals(CodeEditor.Document.Text, _loadedDocument.Content, StringComparison.Ordinal))
            {
                SyncDocumentContentToCodeEditor();
            }
        }
    }

    private void SyncDocumentContentToCodeEditor()
    {
        if (_loadedDocument is null
            || string.Equals(CodeEditor.Document.Text, _loadedDocument.Content, StringComparison.Ordinal))
        {
            return;
        }

        _suppressEditorChange = true;
        try
        {
            CodeEditor.Document.Text = _loadedDocument.Content;
        }
        finally
        {
            _suppressEditorChange = false;
        }
    }

    private void SyncEditorToActiveDocumentBeforePreview()
    {
        if (_suppressEditorChange || _loadedDocument is null)
        {
            return;
        }

        // Capture AvalonEdit buffer before hiding Code surface.
        var text = CodeEditor.Document.Text;
        if (!string.Equals(_loadedDocument.Content, text, StringComparison.Ordinal))
        {
            _loadedDocument.Content = text;
        }
    }

    private void ApplyReadOnlyState()
    {
        CodeEditor.IsReadOnly = _loadedDocument?.IsReadOnly ?? true;
    }

    private void UpdateEditorSurfaceMode()
    {
        var document = _loadedDocument;
        var canPreview = document?.CanPreview == true;
        var showPreview = document?.ShowPreview == true;
        var showMarkdown = showPreview && document!.IsMarkdownFile;
        var showHtml = showPreview && document!.IsHtmlFile;

        ViewModeBar.Visibility = canPreview ? Visibility.Visible : Visibility.Collapsed;

        CodeEditor.Visibility = showPreview ? Visibility.Collapsed : Visibility.Visible;
        MarkdownPreview.Visibility = showMarkdown ? Visibility.Visible : Visibility.Collapsed;
        HtmlPreviewHost.Visibility = showHtml ? Visibility.Visible : Visibility.Collapsed;

        if (!showMarkdown)
        {
            MarkdownPreview.Markdown = string.Empty;
        }

        if (!showHtml)
        {
            ClearHtmlPreview();
        }
    }

    private void RefreshPreviewSurface(bool immediate = false)
    {
        if (_loadedDocument is null || !_loadedDocument.ShowPreview)
        {
            _previewRefreshTimer.Stop();
            return;
        }

        if (immediate)
        {
            _previewRefreshTimer.Stop();
            ApplyPreviewContent();
            return;
        }

        _previewRefreshTimer.Stop();
        _previewRefreshTimer.Start();
    }

    private void OnPreviewRefreshTimerTick(object? sender, EventArgs e)
    {
        _previewRefreshTimer.Stop();
        ApplyPreviewContent();
    }

    private void ApplyPreviewContent()
    {
        if (_loadedDocument is not { ShowPreview: true } document)
        {
            return;
        }

        if (document.IsMarkdownFile)
        {
            if (!string.Equals(MarkdownPreview.Markdown, document.Content, StringComparison.Ordinal))
            {
                MarkdownPreview.Markdown = document.Content;
            }

            return;
        }

        if (document.IsHtmlFile)
        {
            _ = LoadHtmlPreviewAsync(document.Content);
        }
    }

    private async Task LoadHtmlPreviewAsync(string content)
    {
        if (string.Equals(_lastHtmlPreviewContent, content, StringComparison.Ordinal)
            && HtmlPreviewError.Visibility != Visibility.Visible)
        {
            return;
        }

        try
        {
            await WebView2Initializer.EnsureCoreWebView2Async(HtmlPreviewWebView).ConfigureAwait(true);
            if (HtmlPreviewWebView.CoreWebView2 is null)
            {
                throw new InvalidOperationException("WebView2 未初始化。");
            }

            if (!_htmlPreviewReady)
            {
                HtmlPreviewWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                HtmlPreviewWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _htmlPreviewReady = true;
            }

            HtmlPreviewWebView.NavigateToString(MarkdownHtmlRenderer.BuildPreviewDocument(content));
            _lastHtmlPreviewContent = content;
            HtmlPreviewError.Visibility = Visibility.Collapsed;
            HtmlPreviewWebView.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            _lastHtmlPreviewContent = null;
            HtmlPreviewWebView.Visibility = Visibility.Collapsed;
            HtmlPreviewErrorText.Text = Strings.Format("Preview_HtmlFailedMessage", exception.Message);
            HtmlPreviewError.Visibility = Visibility.Visible;
        }
    }

    private void ClearHtmlPreview()
    {
        HtmlPreviewError.Visibility = Visibility.Collapsed;
        HtmlPreviewErrorText.Text = string.Empty;
        // Keep WebView instance; only hide host. Avoid NavigateToString(empty) flicker.
        _lastHtmlPreviewContent = null;
    }

    private void OnCodeEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorChange || _loadedDocument is null || _loadedDocument.ShowPreview)
        {
            return;
        }

        _loadedDocument.Content = CodeEditor.Document.Text;
    }
}
