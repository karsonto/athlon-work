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
}
