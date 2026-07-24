using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.Tests;

public sealed class EditorDocumentViewModelTests
{
    [Fact]
    public void Markdown_file_defaults_to_preview_mode()
    {
        var document = new EditorDocumentViewModel(
            @"C:\workspace\README.md",
            "# Title",
            "README.md");

        Assert.True(document.IsMarkdownFile);
        Assert.True(document.CanPreview);
        Assert.Equal(EditorViewMode.Preview, document.ViewMode);
        Assert.True(document.ShowPreview);
    }

    [Fact]
    public void Html_file_defaults_to_preview_mode()
    {
        var document = new EditorDocumentViewModel(
            @"C:\workspace\index.html",
            "<html></html>",
            "index.html");

        Assert.True(document.IsHtmlFile);
        Assert.True(document.CanPreview);
        Assert.Equal(EditorViewMode.Preview, document.ViewMode);
        Assert.True(document.ShowPreview);
    }

    [Fact]
    public void Code_file_defaults_to_code_mode_without_preview()
    {
        var document = new EditorDocumentViewModel(
            @"C:\workspace\Program.cs",
            "class Program {}",
            "Program.cs");

        Assert.False(document.CanPreview);
        Assert.Equal(EditorViewMode.Code, document.ViewMode);
        Assert.False(document.ShowPreview);
    }

    [Fact]
    public void ShowPreview_follows_view_mode_for_previewable_files()
    {
        var document = new EditorDocumentViewModel(
            @"C:\workspace\notes.markdown",
            "hello",
            "notes.markdown");

        Assert.True(document.ShowPreview);

        document.ViewMode = EditorViewMode.Code;
        Assert.False(document.ShowPreview);

        document.ViewMode = EditorViewMode.Preview;
        Assert.True(document.ShowPreview);
    }

    [Fact]
    public void PathLabel_prefers_relative_path()
    {
        var withRelative = new EditorDocumentViewModel(
            @"C:\workspace\docs\README.md",
            "#",
            "docs/README.md");
        Assert.Equal("docs/README.md", withRelative.PathLabel);

        var withoutRelative = new EditorDocumentViewModel(
            @"C:\workspace\docs\README.md",
            "#",
            null);
        Assert.Equal("README.md", withoutRelative.PathLabel);
    }
}
