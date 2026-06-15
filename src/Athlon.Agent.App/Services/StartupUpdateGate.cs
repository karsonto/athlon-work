using System.Windows;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

/// <summary>Runs update check before SSO / License gates so startup can restart early.</summary>
internal static class StartupUpdateGate
{
    public static void CheckBeforeStartupGates(AppSettings settings)
    {
#if DEBUG
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppUpdateCoordinator.UpdateUrlEnvironmentVariable)))
        {
            return;
        }
#endif

        try
        {
            if (!AppUpdateCoordinator.TryResolveUpdateBaseUrl(settings, out var baseUrl, out _))
            {
                return;
            }

            var updateInfo = Task.Run(() =>
                    AppUpdateCoordinator.CheckForUpdatesAsync(baseUrl))
                .GetAwaiter()
                .GetResult();
            if (updateInfo is null)
            {
                return;
            }

            var result = MessageBox.Show(
                $"发现新版本 {updateInfo.TargetFullRelease.Version}，是否现在下载并安装？",
                AppVersionInfo.ProductName,
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            Task.Run(() => AppUpdateCoordinator.DownloadAndApplyAsync(baseUrl, updateInfo))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Update check before startup gates failed: {ex}");
        }
    }
}
