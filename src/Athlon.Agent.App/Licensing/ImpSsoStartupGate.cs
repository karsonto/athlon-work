using System.Diagnostics;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
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

        using var loginCts = new CancellationTokenSource();
        ImpSsoLoginWaitingWindow? waitingWindow = null;
        try
        {
            waitingWindow = new ImpSsoLoginWaitingWindow();
            waitingWindow.Closing += (_, _) => loginCts.Cancel();
            waitingWindow.Show();

            return RunBrowserLoginWithMessagePump(settings, store, loginCts.Token);
        }
        catch (Exception ex)
        {
            ShowStartupMessage(
                $"IMP SSO 登录失败：{ex.Message}",
                "Athlon Agent",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
        finally
        {
            waitingWindow?.Close();
        }
    }

    /// <summary>
    /// Runs SSO login off the UI thread while pumping the dispatcher so the waiting window stays responsive.
    /// Blocking <c>GetResult()</c> on the UI thread would deadlock when async continuations capture the WPF sync context.
    /// </summary>
    private static bool RunBrowserLoginWithMessagePump(
        SsoSettings settings,
        ImpSsoSessionStore store,
        CancellationToken cancellationToken)
    {
        var loginTask = Task.Run(async () =>
            await PerformBrowserLoginAsync(settings, store, cancellationToken).ConfigureAwait(false),
            cancellationToken);

        var frame = new DispatcherFrame();
        loginTask.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Dispatcher.PushFrame(frame);

        if (loginTask.IsCanceled)
        {
            return false;
        }

        if (loginTask.IsFaulted)
        {
            ExceptionDispatchInfo.Capture(loginTask.Exception!.GetBaseException()).Throw();
        }

        return loginTask.Result;
    }

    private static async Task<bool> PerformBrowserLoginAsync(
        SsoSettings settings,
        ImpSsoSessionStore store,
        CancellationToken cancellationToken)
    {
        using var callbackServer = new ImpSsoCallbackServer(settings);
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var authService = new ImpSsoAuthService(httpClient);

            var waitTask = callbackServer.WaitForCallbackAsync(LoginTimeout, cancellationToken);
            var loginUrl = authService.BuildImpLoginUrl(settings);
            Process.Start(new ProcessStartInfo
            {
                FileName = loginUrl,
                UseShellExecute = true
            });

            var payload = await waitTask.ConfigureAwait(false);
            var result = await authService.CompleteLoginAsync(payload, settings, cancellationToken)
                .ConfigureAwait(false);

            await callbackServer.CompleteBrowserResponseAsync(result, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsValid && result.Session is not null)
            {
                store.SaveSession(result.Session);
                return true;
            }

            return HandleFailure(settings, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await callbackServer.AbortPendingBrowserResponseAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            await callbackServer.StopAsync().ConfigureAwait(false);
        }
    }

    private static bool HandleFailure(SsoSettings settings, ImpSsoCheckResult result)
    {
        if (result.Status == ImpSsoCheckStatus.NoRole)
        {
            ShowStartupMessage(
                result.Message,
                "Athlon Agent",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            OpenUrl($"https://{settings.ImpDomain}/icbcasia/imp/index.html?noRoleForSubApp=true");
            return false;
        }

        ShowStartupMessage(
            string.IsNullOrWhiteSpace(result.Message) ? "IMP SSO 登录失败。" : result.Message,
            "Athlon Agent",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        return false;
    }

    private static void ShowStartupMessage(
        string message,
        string caption,
        MessageBoxButton button,
        MessageBoxImage image)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            MessageBox.Show(message, caption, button, image);
            return;
        }

        dispatcher.Invoke(() => MessageBox.Show(message, caption, button, image));
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
