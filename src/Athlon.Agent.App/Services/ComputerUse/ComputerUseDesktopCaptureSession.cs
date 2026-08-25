using System.Windows;

namespace Athlon.Agent.App.Services.ComputerUse;

/// <summary>
/// Minimizes the Athlon shell so Computer Use BitBlt captures the real desktop,
/// matching interactive Computer Use (main window minimized before observe/interact).
/// </summary>
public interface IComputerUseDesktopCaptureSession
{
    Task<IAsyncDisposable> BeginAsync(CancellationToken cancellationToken = default);
}

public sealed class ComputerUseDesktopCaptureSession : IComputerUseDesktopCaptureSession
{
    public async Task<IAsyncDisposable> BeginAsync(CancellationToken cancellationToken = default)
    {
        var app = Application.Current;
        if (app?.Dispatcher is null)
        {
            return NoopDisposable.Instance;
        }

        WindowState? previousState = null;
        var minimized = false;
        await app.Dispatcher.InvokeAsync(() =>
        {
            var main = app.MainWindow;
            if (main is null)
            {
                return;
            }

            previousState = main.WindowState == WindowState.Minimized
                ? WindowState.Normal
                : main.WindowState;
            if (main.WindowState != WindowState.Minimized)
            {
                main.WindowState = WindowState.Minimized;
                minimized = true;
            }
        });

        if (minimized)
        {
            // Allow the minimize animation / DWM to settle before BitBlt.
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        return new Restorer(app, previousState);
    }

    private sealed class Restorer(Application app, WindowState? previousState) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (previousState is null)
            {
                return;
            }

            try
            {
                await app.Dispatcher.InvokeAsync(() =>
                {
                    var main = app.MainWindow;
                    if (main is null)
                    {
                        return;
                    }

                    var restore = previousState.Value == WindowState.Minimized
                        ? WindowState.Normal
                        : previousState.Value;
                    if (main.WindowState == WindowState.Minimized)
                    {
                        main.WindowState = restore;
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // Dispatcher / window already shutting down.
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
