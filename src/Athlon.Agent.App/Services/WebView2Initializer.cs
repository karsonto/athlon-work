using Microsoft.Web.WebView2.Wpf;

namespace Athlon.Agent.App.Services;

internal static class WebView2Initializer
{
    public static async Task EnsureCoreWebView2Async(
        WebView2 webView,
        CancellationToken cancellationToken = default)
    {
        if (webView.CoreWebView2 is not null)
        {
            return;
        }

        var provider = WebView2ServiceAccess.TryResolve();
        var bundledEnvironment = provider is null
            ? null
            : await provider.TryGetBundledEnvironmentAsync(cancellationToken).ConfigureAwait(true);

        if (bundledEnvironment is not null)
        {
            await webView.EnsureCoreWebView2Async(bundledEnvironment).ConfigureAwait(true);
            return;
        }

        if (WebView2RuntimePolicy.ShouldUseBundledRuntime())
        {
            App.StartupTrace("WebView2 using default Evergreen initialization after bundled runtime unavailable");
        }
        else
        {
            App.StartupTrace("WebView2 using default Evergreen initialization");
        }

        await webView.EnsureCoreWebView2Async().ConfigureAwait(true);
    }

    /// <summary>
    /// Initializes a workspace Browser tab on the shared persistent profile.
    /// Does not fall back to the executable-adjacent user data folder.
    /// </summary>
    public static async Task EnsureBrowserCoreWebView2Async(
        WebView2 webView,
        CancellationToken cancellationToken = default)
    {
        if (webView.CoreWebView2 is not null)
        {
            return;
        }

        var provider = WebView2ServiceAccess.TryResolve()
            ?? throw new InvalidOperationException("Browser WebView2 environment is not available.");
        var environment = await provider.GetBrowserEnvironmentAsync(cancellationToken).ConfigureAwait(true);
        await webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
    }
}
