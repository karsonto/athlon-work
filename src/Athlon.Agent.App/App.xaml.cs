using System.IO;
using System.Windows;
using Athlon.Agent.App.Licensing;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTrace("OnStartup entered");
        base.OnStartup(e);
        AppThemeManager.Apply(AppThemeKind.Dark);
        // License activation runs before MainWindow exists; default OnLastWindowClose would
        // shut down the app when the modal dialog closes.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            if (!LicenseStartupGate.EnsureLicensed())
            {
                Shutdown(-1);
                return;
            }

            StartupTrace("License gate passed");

            var services = new ServiceCollection();
            StartupTrace("ServiceCollection created");
            services.AddAthlonInfrastructure();
            StartupTrace("Infrastructure registered");
            services.AddSingleton(_ => new SessionUiCache(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            services.AddSingleton<SessionTurnHost>();
            services.AddSingleton<QueuedTurnPresenter>();
            services.AddSingleton<ComposerAtCompletionService>();
            services.AddSingleton<ApplicationShutdownService>();
            services.AddSingleton<ClipboardImageAttachmentReader>();
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();
            _services = services.BuildServiceProvider();
            StartupTrace("ServiceProvider built");

            var settings = _services.GetRequiredService<AppSettings>();
            AppThemeManager.ApplyFromSettings(settings.Ui);
            StartupTrace($"Theme applied: {AppThemeManager.CurrentKind}");

            StartupTrace("Resolving MainWindow...");
            MainWindow = _services.GetRequiredService<MainWindow>();
            StartupTrace("MainWindow resolved");
            MainWindow.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            StartupTrace("MainWindow shown");
        }
        catch (Exception exception)
        {
            StartupTrace(exception.ToString());
            MessageBox.Show(
                exception.ToString(),
                "Athlon Agent failed to start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StartupTrace($"OnExit {e.ApplicationExitCode}");
        try
        {
            if (!ApplicationShutdownState.IsCompleted
                && _services?.GetService<ApplicationShutdownService>() is { } shutdownService)
            {
                shutdownService
                    .ShutdownAsync(progress: null, turnWaitTimeout: ApplicationShutdownService.DefaultTurnWaitTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch (Exception ex)
        {
            StartupTrace($"OnExit cleanup failed: {ex}");
        }

        if (_services is not null)
        {
            _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }

    internal static void StartupTrace(string message)
    {
        var paths = new AppPathProvider();
        paths.EnsureCreated();
        File.AppendAllText(Path.Combine(paths.LogsPath, "startup.log"), $"{AppTimeZone.Now:O} {message}{Environment.NewLine}");
    }
}

