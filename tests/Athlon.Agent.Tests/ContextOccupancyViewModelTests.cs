using System.Windows.Media;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class ContextOccupancyViewModelTests
{
    [Fact]
    public void Apply_FreezesDashArray_SoItCanBindAcrossThreads()
    {
        var occupancy = new ContextOccupancyViewModel();
        var budget = new ContextBudgetSnapshot(100_000, 1_000, 1_000, 90_000, 50_000, 0.5, 2_000, 1_000, 500);

        occupancy.Apply(budget, ContextPressureLevel.Elevated);

        Assert.True(occupancy.IsVisible);
        Assert.True(occupancy.RingDashArray.IsFrozen);
        occupancy.ApplyOverflow();
        Assert.True(occupancy.RingDashArray.IsFrozen);
    }

    [Fact]
    public void Apply_FromBackgroundThread_ProducesFrozenDashArray()
    {
        var occupancy = new ContextOccupancyViewModel();
        var budget = new ContextBudgetSnapshot(100_000, 1_000, 1_000, 90_000, 50_000, 0.5);
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                occupancy.Apply(budget, ContextPressureLevel.High);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.True(occupancy.RingDashArray.IsFrozen);
        Assert.Equal(2, occupancy.RingDashArray.Count);
    }
}
