using Athlon.Agent.App;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class LayoutCoordinatorTests
{
    [Fact]
    public void SetContextSidebarVisible_ClampsWidthWhenOpening()
    {
        var storage = new NoOpStorage();
        var settings = new AppSettings
        {
            Ui =
            {
                ContextSidebarVisible = false,
                ContextSidebarWidth = 0
            }
        };
        var coordinator = new LayoutCoordinator(storage, settings);
        var notifications = 0;

        coordinator.SetContextSidebarVisible(true, () => notifications++);

        Assert.True(settings.Ui.ContextSidebarVisible);
        Assert.True(settings.Ui.ContextSidebarWidth >= UiLayoutConstraints.ContextSidebarMinWidth);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void UpdateNavigationSidebarWidth_ClampsToMax()
    {
        var storage = new NoOpStorage();
        var settings = new AppSettings();
        var coordinator = new LayoutCoordinator(storage, settings);

        coordinator.UpdateNavigationSidebarWidth(9999);

        Assert.Equal(UiLayoutConstraints.NavigationSidebarMaxWidth, settings.Ui.NavigationSidebarWidth);
    }
}
