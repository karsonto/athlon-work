using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.Tests;

public sealed class AppThemeResourceBuilderTests
{
    [Fact]
    public void BuildApplicationResources_exposes_brush_keys_at_root()
    {
        RunOnStaThread(() =>
        {
            var resources = AppThemeResourceBuilder.BuildApplicationResources(DarkAppThemePalette.Create());

            Assert.IsType<SolidColorBrush>(resources["Brush.Text"]);
            Assert.IsType<SolidColorBrush>(resources["Brush.SubtleText"]);
            Assert.NotNull(resources["Brush.AppBackground"]);
        });
    }

    [Fact]
    public void BuildApplicationResources_allows_textblock_measure_before_main_window()
    {
        RunOnStaThread(() =>
        {
            var app = new Application();
            app.Resources = AppThemeResourceBuilder.BuildApplicationResources(DarkAppThemePalette.Create());

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
