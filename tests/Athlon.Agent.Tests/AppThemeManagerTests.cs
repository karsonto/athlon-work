using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class AppThemeManagerTests
{
    private static readonly Lazy<Dispatcher> StaDispatcher = new(StartStaDispatcher);

    [Fact]
    public async Task Apply_raises_theme_changed_on_ui_thread_when_called_from_background_thread()
    {
        var dispatcher = EnsureApplicationDispatcher();

        var raised = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerRanOnUiThread = false;
        EventHandler handler = (_, _) =>
        {
            handlerRanOnUiThread = dispatcher.CheckAccess();
            raised.TrySetResult(true);
        };

        dispatcher.Invoke(() => AppThemeManager.ThemeChanged += handler);
        try
        {
            // Apply is invoked off the UI thread while an Application exists; the
            // ThemeChanged handlers must still run on the UI thread.
            await Task.Run(() => AppThemeManager.Apply(AppThemeKind.Light)).ConfigureAwait(false);

            var completed = await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            Assert.Same(raised.Task, completed);
            Assert.True(handlerRanOnUiThread, "ThemeChanged must be raised on the UI thread.");
        }
        finally
        {
            dispatcher.Invoke(() => AppThemeManager.ThemeChanged -= handler);
            dispatcher.Invoke(() => AppThemeManager.Apply(AppThemeKind.Dark));
        }
    }

    private static Dispatcher EnsureApplicationDispatcher()
    {
        return StaDispatcher.Value.Invoke(() =>
        {
            if (System.Windows.Application.Current is { } existing)
            {
                return existing.Dispatcher;
            }

            var app = new global::Athlon.Agent.App.App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            return app.Dispatcher;
        });
    }

    private static Dispatcher StartStaDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            ready.SetResult(dispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
