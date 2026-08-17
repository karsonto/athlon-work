using System.Windows;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.ViewModels;

public partial class MainShellViewModel
{
    private async Task SaveCurrentSessionIfNeededAsync() =>
        await SaveCurrentSessionIfNeededAsync(_session);

    private async Task SaveCurrentSessionIfNeededAsync(AgentSession session)
    {
        await SaveSessionCoreAsync(session);
        await RefreshSessionHistoryAsync();
    }

    private async Task SaveSessionCoreAsync(AgentSession session)
    {
        _runtime.UpdateSession(session);
        await _runtime.FlushSessionAsync(session.Id);
        if (string.Equals(session.Id, _displayedSessionId, StringComparison.Ordinal)
            && _runtime.TryGetHydrated(session.Id, out var live)
            && live.Session is not null)
        {
            _session = live.Session;
        }
    }

    private async Task SaveSessionInBackgroundAsync(AgentSession session)
    {
        try
        {
            await SaveSessionCoreAsync(session);
            RequestRefreshSessionHistory();
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
                ShowShellToast(_loc.Format("Shell_SaveConversationFailed", ex.Message), ShellToastKind.Error));
        }
    }
}
