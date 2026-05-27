using System.IO;
using System.Windows;
using Athlon.Agent.App.ViewModels;
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
        try
        {
            if (_services?.GetService<MainWindowViewModel>() is { } viewModel)
            {
                viewModel.PrepareForShutdown();
            }

            if (_services?.GetService<IMcpRegistry>() is IAsyncDisposable mcpRegistry)
            {
                mcpRegistry.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            StartupTrace($"OnExit cleanup failed: {ex}");
        }

        _services?.Dispose();
        base.OnExit(e);
    }

    internal static void StartupTrace(string message)
    {
        var paths = new AppPathProvider();
        paths.EnsureCreated();
        File.AppendAllText(Path.Combine(paths.LogsPath, "startup.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }
}

