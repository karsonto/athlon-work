using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class WebView2RuntimePolicyTests
{
    [Theory]
    [InlineData(19045, true)]
    [InlineData(21999, true)]
    [InlineData(22000, false)]
    [InlineData(22631, false)]
    public void ShouldUseBundledRuntime_MatchesWindowsBuildThreshold(int osBuild, bool expected) =>
        Assert.Equal(expected, WebView2RuntimePolicy.ShouldUseBundledRuntime(osBuild));

    [Fact]
    public void BrowserUserDataFolder_IsSeparateFromBundledProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-webview");
        var browser = WebView2EnvironmentProvider.BrowserUserDataFolder(root);
        var bundled = WebView2EnvironmentProvider.BundledUserDataFolder(root);

        Assert.Equal(Path.Combine(root, "webview2", "browser"), browser);
        Assert.NotEqual(browser, bundled);
    }
}
