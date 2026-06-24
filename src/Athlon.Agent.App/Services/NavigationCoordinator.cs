using System.Windows;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

public sealed class NavigationCoordinator
{
    private readonly AppSettings _appSettings;
    private readonly IImpSsoSessionStore? _ssoSessionStore;

    public NavigationCoordinator(AppSettings appSettings, IImpSsoSessionStore? ssoSessionStore)
    {
        _appSettings = appSettings;
        _ssoSessionStore = ssoSessionStore;
    }

    public (string DisplayName, bool IsVisible) GetSsoDisplayState()
    {
        if (!_appSettings.Sso.Enabled || _ssoSessionStore is null)
        {
            return (string.Empty, false);
        }

        var session = _ssoSessionStore.GetCachedSession();
        if (session is null || _ssoSessionStore.IsExpired(session))
        {
            return (string.Empty, false);
        }

        var displayName = session.DisplayName;
        return (displayName, !string.IsNullOrWhiteSpace(displayName));
    }

    public void HandlePageChanged(
        string page,
        SettingsViewModel settings,
        ScheduleViewModel schedule,
        KnowledgeViewModel knowledge)
    {
        if (string.Equals(page, "Settings", StringComparison.Ordinal))
        {
            settings.SyncSkillsFromCatalog();
        }
        else if (string.Equals(page, "Schedule", StringComparison.Ordinal))
        {
            schedule.SyncFromSettings();
        }
        else if (string.Equals(page, "Knowledge", StringComparison.Ordinal))
        {
            _ = knowledge.RefreshIfStaleAsync();
        }
    }

    public bool TryConfirmSsoLogout()
    {
        if (!_appSettings.Sso.Enabled)
        {
            return false;
        }

        return MessageBox.Show(
            "确定要退出登录吗？",
            AppVersionInfo.ProductName,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public void ClearSsoSession() => _ssoSessionStore?.Clear();
}
