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
        var toSave = await _sessionNavigation.SaveIfNotEmptyAsync(session);
        if (toSave is null)
        {
            return;
        }

        if (string.Equals(toSave.Id, _displayedSessionId, StringComparison.Ordinal))
        {
            _session = toSave;
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
