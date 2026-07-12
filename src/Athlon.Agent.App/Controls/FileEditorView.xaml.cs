using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _markdownPreviewTimer;

    public FileEditorView()
    {
        InitializeComponent();
        _markdownPreviewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _markdownPreviewTimer.Tick += OnMarkdownPreviewTimerTick;
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
        _markdownPreviewTimer.Stop();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyEditorChrome();
        if (_loadedDocument is null || _loadedDocument.PreferMarkdownPreview)
        {
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
        if (_suppressEditorChange || _loadedDocument is null || _loadedDocument.PreferMarkdownPreview)
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
            RefreshMarkdownPreview();
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

        _suppressEditorChange = true;
        try
        {
            if (document is null)
            {
                CodeEditor.Document.Text = string.Empty;
                CodeEditor.IsReadOnly = true;
                CodeEditor.SyntaxHighlighting = null;
                MarkdownPreview.Markdown = string.Empty;
                UpdateEditorSurfaceMode();
                return;
            }

            CodeEditor.Document.Text = document.Content;
            CodeEditor.SyntaxHighlighting = EditorSyntaxHighlighting.Resolve(document.FilePath);
            CodeEditor.TextArea.TextView.Redraw();
            CodeEditor.ScrollToHome();
            RefreshMarkdownPreview();
            ApplyReadOnlyState();
            UpdateEditorSurfaceMode();
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
            UpdateEditorSurfaceMode();
            RefreshMarkdownPreview();
            return;
        }

        if (e.PropertyName == nameof(EditorDocumentViewModel.Content))
        {
            RefreshMarkdownPreview();
        }
    }

    private void ApplyReadOnlyState()
    {
        CodeEditor.IsReadOnly = _loadedDocument?.IsReadOnly ?? true;
    }

    private void UpdateEditorSurfaceMode()
    {
        var useMarkdownPreview = _loadedDocument?.PreferMarkdownPreview ?? false;
        CodeEditor.Visibility = useMarkdownPreview ? Visibility.Collapsed : Visibility.Visible;
        MarkdownPreview.Visibility = useMarkdownPreview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshMarkdownPreview()
    {
        if (_loadedDocument is null || !_loadedDocument.PreferMarkdownPreview)
        {
            _markdownPreviewTimer.Stop();
            MarkdownPreview.Markdown = string.Empty;
            return;
        }

        _markdownPreviewTimer.Stop();
        _markdownPreviewTimer.Start();
    }

    private void OnMarkdownPreviewTimerTick(object? sender, EventArgs e)
    {
        _markdownPreviewTimer.Stop();
        if (_loadedDocument is { PreferMarkdownPreview: true } document
            && !string.Equals(MarkdownPreview.Markdown, document.Content, StringComparison.Ordinal))
        {
            MarkdownPreview.Markdown = document.Content;
        }
    }

    private void OnCodeEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorChange || _loadedDocument is null || _loadedDocument.PreferMarkdownPreview)
        {
            return;
        }

        _loadedDocument.Content = CodeEditor.Document.Text;
    }
}
