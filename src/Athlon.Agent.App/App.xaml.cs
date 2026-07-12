using System.IO;
using System.Windows;
using Athlon.Agent.App.Licensing;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Sso;
using Athlon.Agent.Infrastructure.SubAgents;
using Athlon.Agent.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    internal ServiceProvider? Services => _services;

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
            var startupSettings = AppSettingsLoader.Load();
            AppCultureManager.ApplyFromSettings(startupSettings.Ui);
            StartupTrace($"Culture applied: {AppCultureManager.Current.Name}");

            StartupUpdateGate.CheckBeforeStartupGates(startupSettings);
            StartupTrace("Startup update gate passed");

            if (startupSettings.Sso.Enabled)
            {
                if (!ImpSsoStartupGate.EnsureAuthenticated(startupSettings.Sso))
                {
                    Shutdown(-1);
                    return;
                }

                StartupTrace("SSO gate passed");
            }

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
            services.AddSingleton(sp => new SessionUiCache(
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                sp.GetRequiredService<AppSettings>()));
            services.AddSingleton<SessionTurnHost>();
            services.AddSingleton<QueuedTurnPresenter>();
            services.AddSingleton<ComposerAtCompletionService>();
            services.AddSingleton<IComposerSlashCommandRegistry>(sp =>
                new ComposerSlashCommandRegistry(sp.GetServices<IComposerSlashCommand>()));
            services.AddSingleton<ComposerSlashCommandExecutor>();
            services.AddSingleton<SchedulerService>();
            services.AddSingleton<ApplicationShutdownService>();
            services.AddSingleton<AppUpdateService>();
            services.AddSingleton<ClipboardImageAttachmentReader>();
            services.AddAthlonViewModels();
            _services = services.BuildServiceProvider();
            StartupTrace("ServiceProvider built");
            _services.GetRequiredService<SubAgentCompletionContinuationService>();

            if (startupSettings.SubAgent.Enabled)
            {
                _services.GetRequiredService<SubAgentBackgroundExecutor>().Start();
                StartupTrace("SubAgentBackgroundExecutor started");
            }

            var settings = _services.GetRequiredService<AppSettings>();
            AppCultureManager.ApplyFromSettings(settings.Ui);
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
                Strings.Get("Startup_FailedTitle"),
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
        if (Current is App { _services: { } services }
            && services.GetService<IAppLogger>() is { } logger)
        {
            logger.ForContext("Startup").Information("{StartupMessage}", message);
            return;
        }

        var paths = new AppPathProvider();
        paths.EnsureCreated();
        File.AppendAllText(Path.Combine(paths.LogsPath, "startup.log"), $"{AppTimeZone.Now:O} {message}{Environment.NewLine}");
    }
}

