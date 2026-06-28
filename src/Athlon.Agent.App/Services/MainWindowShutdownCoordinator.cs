using System.ComponentModel;
using System.Windows;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Services;

public sealed class MainWindowShutdownCoordinator
{
    private readonly IUserNotifier _notifier;

    public MainWindowShutdownCoordinator(IUserNotifier notifier)
    {
        _notifier = notifier;
    }

    public async Task<bool> TryCloseAsync(Window window, MainShellViewModel viewModel, CancelEventArgs e)
    {
        if (!await viewModel.ConfirmCloseEditorTabsAsync().ConfigureAwait(true))
        {
            e.Cancel = true;
            return false;
        }

        if (viewModel.HasPendingShutdownWork)
        {
            if (!_notifier.ConfirmYesNo("Shell_ExitTitle", "Shell_ExitMessage"))
            {
                e.Cancel = true;
                return false;
            }
        }

        e.Cancel = true;
        if (window is MainWindow mainWindow)
        {
            mainWindow.ShowShutdownOverlay();
        }

        window.IsEnabled = false;

        try
        {
            var progress = new Progress<string>(status =>
            {
                if (window.Dispatcher.CheckAccess())
                {
                    viewModel.ShutdownStatusText = status;
                }
                else
                {
                    window.Dispatcher.InvokeAsync(() => viewModel.ShutdownStatusText = status);
                }
            });
            await viewModel.ShutdownAsync(progress).ConfigureAwait(true);
        }
        catch
        {
            // Proceed with exit even if cleanup fails.
        }

        viewModel.Dispose();
        return true;
    }
}
