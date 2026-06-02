using System.Windows;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;
using Serilog;

namespace Athlon.Agent.App.Services;

public sealed class ApplicationShutdownService(
    SessionTurnHost turnHost,
    ExecuteCommandProcessRegistry processRegistry,
    IMcpRegistry mcpRegistry,
    IFileStorageService storage,
    AppSettings appSettings,
    IAppLogger logger)
{
    public static readonly TimeSpan DefaultTurnWaitTimeout = TimeSpan.FromSeconds(15);

    private bool _mcpDisposed;

    public async Task ShutdownAsync(
        IProgress<string>? progress,
        TimeSpan? turnWaitTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (ApplicationShutdownState.IsCompleted)
        {
            return;
        }

        CloseSecondaryWindows();

        progress?.Report("正在停止生成任务…");
        await turnHost
            .ShutdownAsync(turnWaitTimeout ?? DefaultTurnWaitTimeout, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report("正在结束命令行进程…");
        processRegistry.KillAll();

        progress?.Report("正在保存设置…");
        try
        {
            await storage.SaveSettingsAsync(appSettings, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort persist on exit.
        }

        progress?.Report("正在关闭 MCP 连接…");
        await DisposeMcpAsync().ConfigureAwait(false);

        progress?.Report("正在释放日志…");
        ReleaseLogging();

        ApplicationShutdownState.MarkCompleted();
    }

    private static void CloseSecondaryWindows()
    {
        if (Application.Current is null)
        {
            return;
        }

        var main = Application.Current.MainWindow;
        foreach (Window window in Application.Current.Windows)
        {
            if (window is not null && window != main)
            {
                try
                {
                    window.Close();
                }
                catch
                {
                    // Best effort.
                }
            }
        }
    }

    private async Task DisposeMcpAsync()
    {
        if (_mcpDisposed)
        {
            return;
        }

        if (mcpRegistry is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }

        _mcpDisposed = true;
    }

    private void ReleaseLogging()
    {
        if (logger is IDisposable disposable)
        {
            disposable.Dispose();
        }

        Log.CloseAndFlush();
    }
}
