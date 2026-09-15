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

    /// <summary>
    /// The app merges MaterialDesign's control styles but never installs a MaterialDesign theme, so
    /// nothing else writes the <c>MaterialDesign.Brush.*</c> keys. A missing <c>DynamicResource</c>
    /// paints nothing, which made the DatePicker/Calendar flyout transparent over the window behind
    /// it. This pins the bridge that supplies them.
    /// </summary>
    [Fact]
    public void ApplyPalette_injects_material_design_surface_brushes()
    {
        RunOnStaThread(() =>
        {
            var app = EnsureApplication();
            var root = (ResourceDictionary)app.Resources;

            AppThemeResourceBuilder.ApplyPalette(root, DarkAppThemePalette.Create().Chrome);

            var surface = Assert.IsType<SolidColorBrush>(
                root[MaterialDesignBrushBridge.SurfaceKey]);
            // The Calendar flyout paints its fill from this key, so an opaque color is the whole
            // point: a translucent surface reproduces the original overlap bug.
            Assert.Equal(0xFF, surface.Color.A);
            Assert.IsType<SolidColorBrush>(root[MaterialDesignBrushBridge.ForegroundKey]);
            Assert.IsType<SolidColorBrush>(root[MaterialDesignBrushBridge.PrimaryKey]);
            Assert.IsType<SolidColorBrush>(root[MaterialDesignBrushBridge.PrimaryForegroundKey]);
        });
    }

    /// <summary>
    /// Every surface brush the bridge supplies must be fully opaque; otherwise a MaterialDesign
    /// control could paint a see-through background exactly like the calendar flyout did.
    /// </summary>
    [Fact]
    public void Bridge_surface_brushes_are_opaque()
    {
        RunOnStaThread(() =>
        {
            var brushes = MaterialDesignBrushBridge.Build(DarkAppThemePalette.Create().Chrome);

            Assert.NotEmpty(brushes);
            Assert.All(brushes, pair => Assert.IsType<SolidColorBrush>(pair.Value));

            var surface = (SolidColorBrush)brushes[MaterialDesignBrushBridge.SurfaceKey];
            Assert.Equal(0xFF, surface.Color.A);
        });
    }

    /// <summary>
    /// Both themes must supply the keys: the flyout is themed at runtime, so a key present only in
    /// the dark palette would leave the calendar transparent after switching to light.
    /// </summary>
    [Theory]
    [InlineData(AppThemeKind.Dark)]
    [InlineData(AppThemeKind.Light)]
    public void Bridge_supplies_surface_key_for_both_themes(AppThemeKind kind)
    {
        RunOnStaThread(() =>
        {
            var chrome = AppThemeManager.GetPalette(kind).Chrome;
            var brushes = MaterialDesignBrushBridge.Build(chrome);

            var surface = Assert.IsType<SolidColorBrush>(brushes[MaterialDesignBrushBridge.SurfaceKey]);
            Assert.Equal(0xFF, surface.Color.A);
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
