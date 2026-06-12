using System.Windows;
using Athlon.Agent.Core;
using Velopack;
using Velopack.Sources;

namespace Athlon.Agent.App.Services;

internal sealed class AppUpdateService
{
    private const string UpdateUrlEnvironmentVariable = "ATHLON_UPDATE_URL";

    private readonly AppSettings _settings;

    public AppUpdateService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task CheckOnStartupAsync()
    {
#if DEBUG
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UpdateUrlEnvironmentVariable)))
        {
            return;
        }
#endif

        try
        {
            var updateInfo = await CheckForUpdatesAsync();
            if (updateInfo is null)
            {
                return;
            }

            if (!TryResolveUpdateBaseUrl(out var baseUrl, out _))
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var result = MessageBox.Show(
                    $"发现新版本 {updateInfo.TargetFullRelease.Version}，是否现在下载并安装？",
                    AppVersionInfo.ProductName,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    _ = DownloadAndApplyAsync(baseUrl, updateInfo);
                }
            });
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Update check on startup failed: {ex}");
        }
    }

    public async Task<AppUpdateCheckResult> CheckAndPromptAsync()
    {
        if (!TryResolveUpdateBaseUrl(out var baseUrl, out var skipReason))
        {
            return AppUpdateCheckResult.Skipped(skipReason);
        }

        try
        {
            var updateInfo = await CheckForUpdatesAsync(baseUrl);
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

            await DownloadAndApplyAsync(baseUrl, updateInfo);
            return AppUpdateCheckResult.UpdateApplied();
        }
        catch (Exception ex)
        {
            return AppUpdateCheckResult.Failed(ex.Message);
        }
    }

    private async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        if (!TryResolveUpdateBaseUrl(out var baseUrl, out _))
        {
            return null;
        }

        return await CheckForUpdatesAsync(baseUrl);
    }

    private static async Task<UpdateInfo?> CheckForUpdatesAsync(string baseUrl)
    {
        var manager = CreateUpdateManager(baseUrl);
        if (!manager.IsInstalled)
        {
            return null;
        }

        return await manager.CheckForUpdatesAsync();
    }

    private static async Task DownloadAndApplyAsync(string baseUrl, UpdateInfo updateInfo)
    {
        var manager = CreateUpdateManager(baseUrl);
        await manager.DownloadUpdatesAsync(updateInfo);
        manager.ApplyUpdatesAndRestart(updateInfo);
    }

    private static UpdateManager CreateUpdateManager(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/');
        return new UpdateManager(new SimpleWebSource(normalized));
    }

    private bool TryResolveUpdateBaseUrl(out string baseUrl, out string skipReason)
    {
        baseUrl = "";
        skipReason = "";

        var envUrl = Environment.GetEnvironmentVariable(UpdateUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            baseUrl = envUrl.Trim();
            return true;
        }

        if (!_settings.Update.Enabled)
        {
            skipReason = "自动更新已禁用。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_settings.Update.BaseUrl))
        {
            skipReason = "未配置更新源。请在 settings.json 中设置 Update.BaseUrl，或设置环境变量 ATHLON_UPDATE_URL。";
            return false;
        }

        baseUrl = _settings.Update.BaseUrl.Trim();
        return true;
    }
}

internal sealed record AppUpdateCheckResult(AppUpdateCheckStatus Status, string Message)
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

internal enum AppUpdateCheckStatus
{
    Skipped,
    UpToDate,
    UpdateAvailable,
    UpdateApplied,
    Failed,
}
