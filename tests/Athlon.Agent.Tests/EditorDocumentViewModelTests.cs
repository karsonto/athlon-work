using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.Tests;

public sealed class EditorDocumentViewModelTests
{
    [Fact]
    public void PreferMarkdownPreview_is_true_for_read_only_markdown()
    {
        var document = new EditorDocumentViewModel(
            @"C:\workspace\README.md",
            "# Title",
            "README.md",
            isReadOnly: true);

        Assert.True(document.IsMarkdownFile);
        Assert.True(document.PreferMarkdownPreview);
    }

    [Theory]
    [InlineData(@"C:\workspace\README.md", false)]
    [InlineData(@"C:\workspace\Program.cs", true)]
    public void PreferMarkdownPreview_requires_read_only_markdown(string path, bool readOnly)
    {
        var document = new EditorDocumentViewModel(path, "content", null, readOnly);

        Assert.Equal(readOnly && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase), document.PreferMarkdownPreview);
    }
}
