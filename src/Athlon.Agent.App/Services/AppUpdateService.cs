using System.Windows;
using Athlon.Agent.Core;
using Velopack;

namespace Athlon.Agent.App.Services;

public sealed class AppUpdateService
{
    private readonly AppSettings _settings;

    public AppUpdateService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<AppUpdateCheckResult> CheckAndPromptAsync()
    {
        if (!AppUpdateCoordinator.TryResolveUpdateBaseUrl(_settings, out var baseUrl, out var skipReason))
        {
            return AppUpdateCheckResult.Skipped(skipReason);
        }

        try
        {
            var updateInfo = await AppUpdateCoordinator.CheckForUpdatesAsync(baseUrl).ConfigureAwait(false);
            if (updateInfo is null)
            {
                return AppUpdateCheckResult.UpToDate();
            }

            var result = MessageBox.Show(
                $"发现新版本 {updateInfo.TargetFullRelease.Version}，是否现在下载并安装？",
                AppVersionInfo.ProductName,
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
            {
                return AppUpdateCheckResult.UpdateAvailableNotApplied(updateInfo.TargetFullRelease.Version.ToString());
            }

            await AppUpdateCoordinator.DownloadAndApplyAsync(baseUrl, updateInfo).ConfigureAwait(false);
            return AppUpdateCheckResult.UpdateApplied();
        }
        catch (Exception ex)
        {
            return AppUpdateCheckResult.Failed(ex.Message);
        }
    }
}

public sealed record AppUpdateCheckResult(AppUpdateCheckStatus Status, string Message)
{
    public static AppUpdateCheckResult Skipped(string reason) =>
        new(AppUpdateCheckStatus.Skipped, reason);

    public static AppUpdateCheckResult UpToDate() =>
        new(AppUpdateCheckStatus.UpToDate, "当前已是最新版本。");

    public static AppUpdateCheckResult UpdateAvailableNotApplied(string version) =>
        new(AppUpdateCheckStatus.UpdateAvailable, $"发现新版本 {version}，已取消安装。");

    public static AppUpdateCheckResult UpdateApplied() =>
        new(AppUpdateCheckStatus.UpdateApplied, "正在安装更新并重启…");

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
