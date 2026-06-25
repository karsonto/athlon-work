using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.App.Services;

internal static class WebView2ServiceAccess
{
    public static WebView2EnvironmentProvider? TryResolve()
    {
        if (System.Windows.Application.Current is not App { Services: { } services })
        {
            return null;
        }

        return services.GetService<WebView2EnvironmentProvider>();
    }

    public static async Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> GetRequiredEnvironmentAsync(
        CancellationToken cancellationToken = default)
    {
        var provider = TryResolve()
            ?? throw new InvalidOperationException("WebView2EnvironmentProvider is not registered.");
        return await provider.GetEnvironmentAsync(cancellationToken).ConfigureAwait(false);
    }
}
