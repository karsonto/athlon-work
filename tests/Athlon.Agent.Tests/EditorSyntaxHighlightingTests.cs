using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.Tests;

public sealed class EditorSyntaxHighlightingTests
{
    [Theory]
    [InlineData("README.md")]
    [InlineData("notes.markdown")]
    [InlineData("Program.cs")]
    [InlineData("script.py")]
    [InlineData("package.json")]
    [InlineData("index.html")]
    [InlineData("app.tsx")]
    public void Resolve_returns_definition_for_common_file_types(string filePath)
    {
        AppThemeManager.Apply(AppThemeKind.Dark);

        var definition = EditorSyntaxHighlighting.Resolve(filePath);

        Assert.NotNull(definition);
        Assert.False(string.IsNullOrWhiteSpace(definition.Name));
    }
}
