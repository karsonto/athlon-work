using Microsoft.Web.WebView2.Wpf;

namespace Athlon.Agent.App.Services;

internal static class WebView2Initializer
{
    public static async Task EnsureCoreWebView2Async(
        WebView2 webView,
        CancellationToken cancellationToken = default)
    {
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
}
