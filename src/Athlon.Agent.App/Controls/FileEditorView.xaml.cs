using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    public FileEditorView()
    {
        InitializeComponent();
        ApplyEditorChrome();
        CodeEditor.TextChanged += OnCodeEditorTextChanged;
        DataContextChanged += (_, _) => AttachEditor(DataContext as FileEditorViewModel);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => AppThemeManager.ThemeChanged += OnThemeChanged;

    private void OnUnloaded(object sender, RoutedEventArgs e) => AppThemeManager.ThemeChanged -= OnThemeChanged;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyEditorChrome();
        if (_loadedDocument is null)
        {
            return;
        }

        CodeEditor.SyntaxHighlighting = EditorSyntaxHighlighting.Resolve(_loadedDocument.FilePath);
        CodeEditor.TextArea.TextView.Redraw();
    }

    private void ApplyEditorChrome()
    {
        CodeEditor.Background = new SolidColorBrush(EditorSyntaxHighlighting.EditorBackground);
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
        if (_suppressEditorChange || _loadedDocument is null)
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
            return;
        }

        _loadedDocument = document;
        _suppressEditorChange = true;
        try
        {
            if (document is null)
            {
                CodeEditor.Document.Text = string.Empty;
                CodeEditor.IsReadOnly = true;
                CodeEditor.SyntaxHighlighting = null;
                return;
            }

            CodeEditor.IsReadOnly = false;
            CodeEditor.Document.Text = document.Content;
            CodeEditor.SyntaxHighlighting = EditorSyntaxHighlighting.Resolve(document.FilePath);
            CodeEditor.TextArea.TextView.Redraw();
            CodeEditor.ScrollToHome();
        }
        finally
        {
            _suppressEditorChange = false;
        }
    }

    private void OnCodeEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorChange || _loadedDocument is null)
        {
            return;
        }

        _loadedDocument.Content = CodeEditor.Document.Text;
    }

}
