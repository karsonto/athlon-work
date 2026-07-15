using Athlon.Agent.App.Localization;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.BehaviorReport;

namespace Athlon.Agent.App.Services;

public sealed class NavigationCoordinator
{
    private readonly AppSettings _appSettings;
    private readonly IImpSsoSessionStore? _ssoSessionStore;
    private readonly IUserNotifier _notifier;

    public NavigationCoordinator(
        AppSettings appSettings,
        IImpSsoSessionStore? ssoSessionStore,
        IUserNotifier notifier)
    {
        _appSettings = appSettings;
        _ssoSessionStore = ssoSessionStore;
        _notifier = notifier;
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

        return _notifier.ConfirmYesNo("Sso_LogoutTitle", "Sso_LogoutConfirm");
    }

    public void ClearSsoSession()
    {
        try
        {
            BehaviorEventManager.Instance.Record(
                BehaviorEventIds.UserSession,
                BehaviorEventTypes.Event,
                BehaviorEventIds.UserSession,
                new Dictionary<string, object?>
                {
                    ["action"] = "logout",
                    ["reason"] = "manual"
                });
        }
        catch
        {
            // ignore
        }

        _ssoSessionStore?.Clear();
    }
}
