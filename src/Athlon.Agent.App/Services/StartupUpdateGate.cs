using System.Windows;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Infrastructure.BehaviorReport;

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
                RecordUpdateCheck(hasUpdate: false, version: null);
                return;
            }

            var updateInfo = Task.Run(() =>
                    AppUpdateCoordinator.CheckForUpdatesAsync(baseUrl))
                .GetAwaiter()
                .GetResult();
            if (updateInfo is null)
            {
                RecordUpdateCheck(hasUpdate: false, version: null);
                return;
            }

            var version = updateInfo.TargetFullRelease.Version;
            RecordUpdateCheck(hasUpdate: true, version: version.ToString());
            var result = MessageBox.Show(
                Strings.Format("Update_AvailableMessage", version),
                Strings.Get("Update_AvailableTitle"),
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

    private static void RecordUpdateCheck(bool hasUpdate, string? version)
    {
        try
        {
            EventManager.Instance.Record(
                BehaviorEventIds.AppUpdateCheck,
                BehaviorEventTypes.Event,
                BehaviorEventIds.AppUpdateCheck,
                new Dictionary<string, object?>
                {
                    ["has_update"] = hasUpdate,
                    ["version"] = version
                });
        }
        catch
        {
            // ignore
        }
    }
}
