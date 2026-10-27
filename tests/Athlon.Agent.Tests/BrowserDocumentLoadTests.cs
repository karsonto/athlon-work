using Athlon.Agent.App.Services.Browser;

namespace Athlon.Agent.Tests;

public sealed class BrowserDocumentLoadTests
{
    [Fact]
    public void Choose_Https_UsesDefaultNavigate()
    {
        Assert.Equal(
            BrowserDocumentLoad.FileLoad.DefaultNavigate,
            BrowserDocumentLoad.Choose("https://example.com/a", "https://example.com/b"));
    }

    [Fact]
    public void Choose_NewFile_BypassesCache()
    {
        var current = FileUri("previous.html");
        var target = FileUri("next.html");

        Assert.Equal(
            BrowserDocumentLoad.FileLoad.FileNavigateBypassingCache,
            BrowserDocumentLoad.Choose(current, target));
        Assert.Equal(
            BrowserDocumentLoad.FileLoad.FileNavigateBypassingCache,
            BrowserDocumentLoad.Choose(null, target));
    }

    [Fact]
    public void Choose_SameFile_ReloadsIgnoringCache()
    {
        var url = FileUri("demo.html");

        Assert.Equal(
            BrowserDocumentLoad.FileLoad.FileReloadIgnoringCache,
            BrowserDocumentLoad.Choose(url, url));
    }

    [Fact]
    public void Choose_SameFile_IgnoresCaseAndTrailingSeparator()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "athlon-browser-demo.html"));
        var original = new Uri(path).AbsoluteUri;
        var recased = original.Replace("athlon-browser-demo.html", "ATHLON-BROWSER-DEMO.HTML", StringComparison.Ordinal);

        Assert.True(BrowserDocumentLoad.IsSameFile(original, recased));
        Assert.True(BrowserDocumentLoad.IsSameFile(original, original.TrimEnd('/') + "/"));
        Assert.Equal(
            BrowserDocumentLoad.FileLoad.FileReloadIgnoringCache,
            BrowserDocumentLoad.Choose(original, recased));
    }

    private static string FileUri(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), fileName));
        return new Uri(path).AbsoluteUri;
    }
}
