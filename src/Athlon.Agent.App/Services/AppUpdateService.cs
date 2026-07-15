using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Velopack;

namespace Athlon.Agent.App.Services;

public sealed class AppUpdateService
{
    private readonly AppSettings _settings;
    private readonly IUserNotifier _notifier;

    public AppUpdateService(AppSettings settings, IUserNotifier notifier)
    {
        _settings = settings;
        _notifier = notifier;
    }

    public async Task<AppUpdateCheckResult> CheckAndPromptAsync()
    {
        if (!AppUpdateCoordinator.TryResolveUpdateBaseUrl(_settings, out var baseUrl, out var skipReason))
        {
            RecordUpdateCheck(hasUpdate: false, version: null);
            return AppUpdateCheckResult.Skipped(skipReason);
        }

        try
        {
            var updateInfo = await AppUpdateCoordinator.CheckForUpdatesAsync(baseUrl).ConfigureAwait(false);
            if (updateInfo is null)
            {
                RecordUpdateCheck(hasUpdate: false, version: null);
                return AppUpdateCheckResult.UpToDate();
            }

            var version = updateInfo.TargetFullRelease.Version.ToString();
            RecordUpdateCheck(hasUpdate: true, version: version);
            if (!_notifier.ConfirmYesNo("Update_AvailableTitle", "Update_AvailableMessage", version))
            {
                return AppUpdateCheckResult.UpdateAvailableNotApplied(version);
            }

            await AppUpdateCoordinator.DownloadAndApplyAsync(baseUrl, updateInfo).ConfigureAwait(false);
            return AppUpdateCheckResult.UpdateApplied();
        }
        catch (Exception ex)
        {
            return AppUpdateCheckResult.Failed(ex.Message);
        }
    }

    private static void RecordUpdateCheck(bool hasUpdate, string? version)
    {
        try
        {
            BehaviorEventManager.Instance.Record(
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

public sealed record AppUpdateCheckResult(AppUpdateCheckStatus Status, string Message)
{
    public static AppUpdateCheckResult Skipped(string reason) =>
        new(AppUpdateCheckStatus.Skipped, reason);

    public static AppUpdateCheckResult UpToDate() =>
        new(AppUpdateCheckStatus.UpToDate, Strings.Get("Update_UpToDate"));

    public static AppUpdateCheckResult UpdateAvailableNotApplied(string version) =>
        new(AppUpdateCheckStatus.UpdateAvailable, Strings.Format("Update_Cancelled", version));

    public static AppUpdateCheckResult UpdateApplied() =>
        new(AppUpdateCheckStatus.UpdateApplied, Strings.Get("Update_Applying"));

    public static AppUpdateCheckResult Failed(string message) =>
        new(AppUpdateCheckStatus.Failed, message);
}

public enum AppUpdateCheckStatus
{
    Skipped,
    UpToDate,
    UpdateAvailable,
    UpdateApplied,
    Failed,
}
