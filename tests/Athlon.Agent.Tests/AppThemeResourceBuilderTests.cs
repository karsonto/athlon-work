using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class AppThemeResourceBuilderTests
{
    private static readonly Lazy<Dispatcher> StaDispatcher = new(StartStaDispatcher);

    [Fact]
    public void ApplyPalette_inserts_brushes_before_control_styles()
    {
        RunOnStaThread(() =>
        {
            var app = EnsureApplication();
            var root = (ResourceDictionary)app.Resources;

            AppThemeResourceBuilder.ApplyPalette(root, DarkAppThemePalette.Create().Chrome);

            var palette = AppThemeResourceBuilder.FindPaletteDictionary(root);
            Assert.NotNull(palette);
            Assert.Equal(0, root.MergedDictionaries.IndexOf(palette!));
            Assert.IsType<SolidColorBrush>(palette![AppThemeResourceBuilder.TextBrushKey]);
        });
    }

    [Fact]
    public void ApplyPalette_allows_textblock_measure_before_main_window()
    {
        RunOnStaThread(() =>
        {
            var app = EnsureApplication();
            AppThemeManager.Apply(AppThemeKind.Dark);

            var textBlock = new TextBlock { Text = "Athlon Agent" };
            textBlock.Measure(new Size(200, 40));

            Assert.IsType<SolidColorBrush>(textBlock.Foreground);
        });
    }

    private static global::Athlon.Agent.App.App EnsureApplication()
    {
        if (System.Windows.Application.Current is global::Athlon.Agent.App.App existing)
        {
            return existing;
        }

        var app = new global::Athlon.Agent.App.App();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        return app;
    }

    private static void RunOnStaThread(Action action)
    {
        StaDispatcher.Value.Invoke(action);
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
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
