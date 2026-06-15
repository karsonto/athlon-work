using Athlon.Agent.Core;
using Velopack;
using Velopack.Sources;

namespace Athlon.Agent.App.Services;

internal static class AppUpdateCoordinator
{
    internal const string UpdateUrlEnvironmentVariable = "ATHLON_UPDATE_URL";

    public static bool TryResolveUpdateBaseUrl(AppSettings settings, out string baseUrl, out string skipReason)
    {
        baseUrl = "";
        skipReason = "";

        var envUrl = Environment.GetEnvironmentVariable(UpdateUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            baseUrl = envUrl.Trim();
            return true;
        }

        if (!settings.Update.Enabled)
        {
            skipReason = "自动更新已禁用。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.Update.BaseUrl))
        {
            skipReason = "未配置更新源。请在 settings.json 中设置 Update.BaseUrl，或设置环境变量 ATHLON_UPDATE_URL。";
            return false;
        }

        baseUrl = settings.Update.BaseUrl.Trim();
        return true;
    }

    public static async Task<UpdateInfo?> CheckForUpdatesAsync(string baseUrl)
    {
        var manager = CreateUpdateManager(baseUrl);
        if (!manager.IsInstalled)
        {
            return null;
        }

        return await manager.CheckForUpdatesAsync().ConfigureAwait(false);
    }

    public static async Task DownloadAndApplyAsync(string baseUrl, UpdateInfo updateInfo)
    {
        var manager = CreateUpdateManager(baseUrl);
        await manager.DownloadUpdatesAsync(updateInfo).ConfigureAwait(false);
        manager.ApplyUpdatesAndRestart(updateInfo);
    }

    private static UpdateManager CreateUpdateManager(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/');
        return new UpdateManager(new SimpleWebSource(normalized));
    }
}
