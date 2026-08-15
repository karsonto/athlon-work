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

    [Fact]
    public void Apply_RingArc_TracksUtilizationUntilFullCircle()
    {
        var occupancy = new ContextOccupancyViewModel();
        var circumference = ContextOccupancyViewModel.RingCircumference;
        var budget = new ContextBudgetSnapshot(
            100_000,
            0,
            8_000,
            92_000,
            38_000,
            0.41,
            4_000,
            4_000,
            0,
            new ContextOccupancyBreakdown(SystemPrompt: 4_000, ToolDefinitions: 4_000, Conversation: 38_000));

        occupancy.Apply(budget, ContextPressureLevel.Normal);

        Assert.Equal(46, occupancy.PercentUsed);
        Assert.Equal(2, occupancy.RingDashArray.Count);
        Assert.Equal(0.46 * circumference, occupancy.RingDashArray[0], 3);
        Assert.Equal(circumference, occupancy.RingDashArray[0] + occupancy.RingDashArray[1], 3);
        Assert.Contains(occupancy.Categories, row => row.Id == "conversation");
        Assert.DoesNotContain(occupancy.Categories, row => row.Id == "rules");
    }

    [Fact]
    public void FrozenDash_FullUtilization_CoversCircumference()
    {
        var dash = ContextOccupancyViewModel.FrozenDash(ContextOccupancyViewModel.RingCircumference);
        Assert.Equal(ContextOccupancyViewModel.RingCircumference, dash[0], 6);
        Assert.True(dash[1] > 0);
        Assert.True(dash[0] + dash[1] >= ContextOccupancyViewModel.RingCircumference);
    }
}
