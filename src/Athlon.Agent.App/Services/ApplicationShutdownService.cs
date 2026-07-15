using System.Windows;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Athlon.Agent.Mcp;
using Serilog;

namespace Athlon.Agent.App.Services;

public sealed class ApplicationShutdownService(
    SessionTurnHost turnHost,
    SchedulerService scheduler,
    ExecuteCommandProcessRegistry processRegistry,
    IMcpRegistry mcpRegistry,
    IFileStorageService storage,
    AppSettings appSettings,
    IAppLogger logger,
    IEventManager eventManager)
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

        try
        {
            var uptimeMs = (long)(DateTimeOffset.UtcNow - BehaviorEventManager.Instance.StartedAt).TotalMilliseconds;
            eventManager.Record(
                BehaviorEventIds.AppShutdown,
                BehaviorEventTypes.Event,
                BehaviorEventIds.AppShutdown,
                new Dictionary<string, object?>
                {
                    ["reason"] = "shutdown",
                    ["uptime_ms"] = uptimeMs
                });
            await eventManager.FlushAsync(cancellationToken).ConfigureAwait(false);
            eventManager.Stop();
        }
        catch
        {
            // Behavior reporting must never block shutdown.
        }

        CloseSecondaryWindows();

        progress?.Report(Strings.Get("Shutdown_StoppingScheduler"));
        scheduler.Stop();

        progress?.Report(Strings.Get("Shutdown_StoppingTurns"));
        await turnHost
            .ShutdownAsync(turnWaitTimeout ?? DefaultTurnWaitTimeout, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(Strings.Get("Shutdown_FlushingToolLogs"));
        await storage.FlushPendingToolCallLogsAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(Strings.Get("Shutdown_KillingProcesses"));
        processRegistry.KillAll();

        progress?.Report(Strings.Get("Shutdown_SavingSettings"));
        try
        {
            await storage.SaveSettingsAsync(appSettings, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort persist on exit.
        }

        progress?.Report(Strings.Get("Shutdown_ClosingMcp"));
        await DisposeMcpAsync().ConfigureAwait(false);

        progress?.Report(Strings.Get("Shutdown_ReleasingLogs"));
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
