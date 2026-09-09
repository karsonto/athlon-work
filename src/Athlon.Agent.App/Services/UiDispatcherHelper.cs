using System.Windows;
using System.Windows.Threading;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Runs async work on the WPF UI thread. BrowserAutomationHost and TerminalAutomationHost
/// previously each declared an identical private <c>InvokeOnUiAsync</c> overload pair;
/// this helper centralizes that logic.
/// </summary>
public static class UiDispatcherHelper
{
    private static Dispatcher Dispatcher =>
        Application.Current?.Dispatcher
        ?? throw new InvalidOperationException("WPF dispatcher is not available.");

    public static bool OnUiThread => Dispatcher.CheckAccess();

    public static async Task RunAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }

        var op = dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
        await op.Task.ConfigureAwait(false);
        await op.Result.ConfigureAwait(false);
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess())
        {
            return await action().ConfigureAwait(true);
        }

        var op = dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
        await op.Task.ConfigureAwait(false);
        return await op.Result.ConfigureAwait(false);
    }
}
