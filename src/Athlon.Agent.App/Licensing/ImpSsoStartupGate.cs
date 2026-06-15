using System.Diagnostics;
using System.Windows;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Sso;

namespace Athlon.Agent.App.Licensing;

public static class ImpSsoStartupGate
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    public static bool EnsureAuthenticated(SsoSettings settings)
    {
#if DEBUG
        if (string.Equals(
                Environment.GetEnvironmentVariable("ATHLON_SKIP_SSO"),
                "1",
                StringComparison.Ordinal))
        {
            return true;
        }
#endif

        var paths = new AppPathProvider();
        paths.EnsureCreated();
        var store = new ImpSsoSessionStore(paths);

        var cached = store.GetCachedSession();
        if (cached is not null && !store.IsExpired(cached))
        {
            return true;
        }

        store.Clear();

        try
        {
            return PerformBrowserLoginAsync(settings, store).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"IMP SSO 登录失败：{ex.Message}",
                "Athlon Agent",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private static async Task<bool> PerformBrowserLoginAsync(SsoSettings settings, ImpSsoSessionStore store)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var authService = new ImpSsoAuthService(httpClient);
        using var callbackServer = new ImpSsoCallbackServer(settings);

        var waitTask = callbackServer.WaitForCallbackAsync(LoginTimeout);
        var loginUrl = authService.BuildImpLoginUrl(settings);
        Process.Start(new ProcessStartInfo
        {
            FileName = loginUrl,
            UseShellExecute = true
        });

        var payload = await waitTask;
        var result = await authService.CompleteLoginAsync(payload, settings);
        if (result.IsValid && result.Session is not null)
        {
            store.SaveSession(result.Session);
            return true;
        }

        return HandleFailure(settings, result);
    }

    private static bool HandleFailure(SsoSettings settings, ImpSsoCheckResult result)
    {
        if (result.Status == ImpSsoCheckStatus.NoRole)
        {
            MessageBox.Show(
                result.Message,
                "Athlon Agent",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            OpenUrl($"https://{settings.ImpDomain}/icbcasia/imp/index.html?noRoleForSubApp=true");
            return false;
        }

        MessageBox.Show(
            string.IsNullOrWhiteSpace(result.Message) ? "IMP SSO 登录失败。" : result.Message,
            "Athlon Agent",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        return false;
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
