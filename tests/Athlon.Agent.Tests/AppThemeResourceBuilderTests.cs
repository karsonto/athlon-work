using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.Tests;

public sealed class AppThemeResourceBuilderTests
{
    [Fact]
    public void ApplyPalette_inserts_brushes_before_control_styles()
    {
        RunOnStaThread(() =>
        {
            var app = new global::Athlon.Agent.App.App();
            var root = (ResourceDictionary)app.Resources;

            AppThemeResourceBuilder.ApplyPalette(root, DarkAppThemePalette.Create().Chrome);

            var palette = AppThemeResourceBuilder.FindPaletteDictionary(root);
            Assert.NotNull(palette);
            Assert.Equal(0, root.MergedDictionaries.IndexOf(palette!));
            Assert.IsType<SolidColorBrush>(palette![AppThemeResourceBuilder.TextBrushKey]);
            app.Shutdown();
        });
    }

    [Fact]
    public void ApplyPalette_allows_textblock_measure_before_main_window()
    {
        RunOnStaThread(() =>
        {
            var app = new global::Athlon.Agent.App.App();
            AppThemeManager.Apply(AppThemeKind.Dark);

            var textBlock = new TextBlock { Text = "Athlon Agent" };
            textBlock.Measure(new Size(200, 40));

            Assert.IsType<SolidColorBrush>(textBlock.Foreground);
            app.Shutdown();
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test thread did not finish.");
        if (failure is not null)
        {
            throw failure;
        }
    }
}
