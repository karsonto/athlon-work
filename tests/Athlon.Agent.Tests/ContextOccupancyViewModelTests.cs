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
        Assert.Equal(0.46 * circumference / ContextOccupancyViewModel.RingStrokeThickness, occupancy.RingDashArray[0], 3);
        Assert.Equal(circumference / ContextOccupancyViewModel.RingStrokeThickness, occupancy.RingDashArray[0] + occupancy.RingDashArray[1], 3);
        Assert.Contains(occupancy.Categories, row => row.Id == "conversation");
        Assert.DoesNotContain(occupancy.Categories, row => row.Id == "rules");
    }

    [Fact]
    public void FrozenDash_FullUtilization_CoversCircumference()
    {
        var dash = ContextOccupancyViewModel.FrozenDash(ContextOccupancyViewModel.RingCircumference);
        var fullDash = ContextOccupancyViewModel.RingCircumference / ContextOccupancyViewModel.RingStrokeThickness;
        Assert.Equal(fullDash, dash[0], 6);
        Assert.True(dash[1] > 0);
        Assert.True(dash[0] + dash[1] >= fullDash);
    }

    [Fact]
    public void Apply_PercentMatchesTotalUtilization_NotContentSum()
    {
        var occupancy = new ContextOccupancyViewModel();
        // The content categories sum to 60_000 while the 40_000 safety margin is excluded from them,
        // so a content-only share would read 60%. TotalUtilization (80_000) is the metric the
        // pressure evaluator uses, and the ring must agree with it.
        var budget = new ContextBudgetSnapshot(
            100_000,
            0,
            40_000,
            60_000,
            40_000,
            0.66,
            20_000,
            20_000,
            40_000,
            new ContextOccupancyBreakdown(SystemPrompt: 20_000, ToolDefinitions: 20_000, Conversation: 20_000));

        occupancy.Apply(budget, ContextPressureLevel.Critical);

        Assert.Equal(60_000, budget.DisplayedContentTokens);
        Assert.Equal(80_000, budget.EstimatedTotalPrompt);
        Assert.Equal((int)Math.Round(budget.TotalUtilization * 100), occupancy.PercentUsed);
        Assert.Equal(80, occupancy.PercentUsed);
        // The capacity label reports the same numerator the ring fills against.
        Assert.Contains(
            TokenCountDisplay.FormatCompact(budget.EstimatedTotalPrompt),
            occupancy.UsedCapacityLabel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_AtCriticalBoundary_AgreesWithPressureEvaluator()
    {
        var dynamic = new DynamicCompactionSettings();
        var target = dynamic.TargetUtilization;
        var occupancy = new ContextOccupancyViewModel();

        // Craft a budget whose TotalUtilization sits exactly on the Critical boundary.
        const int window = 100_000;
        const int overhead = 0;
        var history = (int)Math.Round(target * window);
        var budget = new ContextBudgetSnapshot(
            window,
            0,
            overhead,
            window,
            history,
            (double)history / window,
            history,
            0,
            0,
            new ContextOccupancyBreakdown(SystemPrompt: history));

        var pressure = ContextPressureEvaluator.Evaluate(budget, dynamic);
        occupancy.Apply(budget, pressure);

        Assert.Equal(ContextPressureLevel.Critical, pressure);
        Assert.Equal((int)Math.Round(target * 100), occupancy.PercentUsed);
        Assert.True(occupancy.IsCompactCtaEmphasized);
    }
}
