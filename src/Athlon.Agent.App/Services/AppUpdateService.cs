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

    /// <summary>
    /// Silent check for the bottom-left update banner. Returns null when there is no update,
    /// updates are disabled/unconfigured, the app is not an installed package, or any error occurs.
    /// </summary>
    public async Task<AppUpdateQuietResult?> CheckQuietAsync(CancellationToken cancellationToken = default)
    {
        if (!AppUpdateCoordinator.TryResolveUpdateBaseUrl(_settings, out var baseUrl, out var skipReason))
        {
            App.StartupTrace($"Update quiet check skipped: {skipReason}");
            RecordUpdateCheck(hasUpdate: false, version: null);
            return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updateInfo = await AppUpdateCoordinator.CheckForUpdatesAsync(baseUrl).ConfigureAwait(false);
            if (updateInfo is null)
            {
                RecordUpdateCheck(hasUpdate: false, version: null);
                return null;
            }

            var version = updateInfo.TargetFullRelease.Version.ToString();
            RecordUpdateCheck(hasUpdate: true, version: version);
            return new AppUpdateQuietResult(updateInfo, version, baseUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Update quiet check failed: {ex.Message}");
            return null;
        }
    }

    public async Task ApplyAsync(AppUpdateQuietResult pending, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        cancellationToken.ThrowIfCancellationRequested();
        await AppUpdateCoordinator.DownloadAndApplyAsync(pending.BaseUrl, pending.UpdateInfo)
            .ConfigureAwait(false);
    }

    public async Task<AppUpdateCheckResult> CheckAndPromptAsync()
    {
        var quiet = await CheckQuietAsync().ConfigureAwait(false);
        if (quiet is null)
        {
            if (!AppUpdateCoordinator.TryResolveUpdateBaseUrl(_settings, out _, out var skipReason)
                && !string.IsNullOrWhiteSpace(skipReason))
            {
                return AppUpdateCheckResult.Skipped(skipReason);
            }

            return AppUpdateCheckResult.UpToDate();
        }

        if (!_notifier.ConfirmYesNo("Update_AvailableTitle", "Update_AvailableMessage", quiet.Version))
        {
            return AppUpdateCheckResult.UpdateAvailableNotApplied(quiet.Version);
        }

        try
        {
            await ApplyAsync(quiet).ConfigureAwait(false);
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

public sealed record AppUpdateQuietResult(UpdateInfo UpdateInfo, string Version, string BaseUrl);

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
