using System.IO;
using System.Windows;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTrace("OnStartup entered");
        base.OnStartup(e);
        try
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            StartupTrace("ShutdownMode configured");

            var services = new ServiceCollection();
            StartupTrace("ServiceCollection created");
            services.AddAthlonInfrastructure();
            StartupTrace("Infrastructure registered");
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();
            _services = services.BuildServiceProvider();
            StartupTrace("ServiceProvider built");

            MainWindow = _services.GetRequiredService<MainWindow>();
            StartupTrace("MainWindow resolved");
            MainWindow.Show();
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
        _services?.Dispose();
        base.OnExit(e);
    }

    internal static void StartupTrace(string message)
    {
        var root = new AppPathProvider().LogsPath;
        Directory.CreateDirectory(root);
        File.AppendAllText(Path.Combine(root, "startup.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }
}

