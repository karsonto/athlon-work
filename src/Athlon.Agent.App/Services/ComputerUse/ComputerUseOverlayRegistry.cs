using Athlon.Agent.App.Windows;

namespace Athlon.Agent.App.Services.ComputerUse;

public sealed class ComputerUseOverlayRegistry
{
    private readonly object _gate = new();
    private ComputerUseOverlayWindow? _window;

    public void Register(ComputerUseOverlayWindow window)
    {
        lock (_gate)
        {
            _window = window;
        }
    }

    public void Unregister(ComputerUseOverlayWindow window)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        }
    }

    public async Task<T> RunWithOverlayHiddenAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        return await RunWithOverlayHiddenAsync(
            _ => Task.FromResult(action()),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> RunWithOverlayHiddenAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ComputerUseOverlayWindow? window;
        lock (_gate)
        {
            window = _window;
        }

        var restore = window is { IsVisible: true };
        if (restore)
        {
            try
            {
                if (window!.Dispatcher.HasShutdownStarted
                    || window.Dispatcher.HasShutdownFinished)
                {
                    restore = false;
                }
                else
                {
                    await window.Dispatcher.InvokeAsync(window.Hide);
                }
            }
            catch (InvalidOperationException)
            {
                restore = false;
            }
            catch (TaskCanceledException)
            {
                restore = false;
            }
        }

        try
        {
            if (restore)
            {
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (restore && window is not null)
            {
                ComputerUseOverlayWindow? current;
                lock (_gate)
                {
                    current = _window;
                }

                try
                {
                    if (ReferenceEquals(current, window)
                        && !window.Dispatcher.HasShutdownStarted
                        && !window.Dispatcher.HasShutdownFinished)
                    {
                        await window.Dispatcher.InvokeAsync(() =>
                        {
                            if (!window.IsVisible)
                            {
                                window.Show();
                            }
                        });
                    }
                }
                catch (InvalidOperationException)
                {
                    // The user closed the overlay while the desktop operation was running.
                }
                catch (TaskCanceledException)
                {
                    // The dispatcher shut down while the desktop operation was running.
                }
            }
        }
    }
}
