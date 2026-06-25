using System.ComponentModel;
using System.Windows;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Services;

public sealed class MainWindowShutdownCoordinator
{
    public async Task<bool> TryCloseAsync(Window window, MainShellViewModel viewModel, CancelEventArgs e)
    {
        if (!await viewModel.ConfirmCloseEditorTabsAsync().ConfigureAwait(true))
        {
            e.Cancel = true;
            return false;
        }

        if (viewModel.HasPendingShutdownWork)
        {
            var confirm = MessageBox.Show(
                window,
                "有对话正在生成或消息排队中，退出将停止所有任务。确定退出？",
                "退出 Athlon Agent",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
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
