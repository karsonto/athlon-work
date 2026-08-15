using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Compaction;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.Tests;

public sealed class ShellStatusFeedbackTests
{
    [Fact]
    public void ShowToast_sets_message_kind_and_visibility()
    {
        var feedback = new ShellStatusFeedback
        {
            DelayAsync = static (_, _) => Task.Delay(Timeout.Infinite)
        };

        feedback.ShowToast("  hello  ", ShellToastKind.Error);

        Assert.True(feedback.IsToastVisible);
        Assert.Equal("hello", feedback.ToastMessage);
        Assert.Equal(ShellToastKind.Error, feedback.ToastKind);
    }

    [Fact]
    public void ShowToast_with_blank_message_hides()
    {
        var feedback = new ShellStatusFeedback
        {
            DelayAsync = static (_, _) => Task.Delay(Timeout.Infinite)
        };
        feedback.ShowToast("visible", ShellToastKind.Info);

        feedback.ShowToast("   ");

        Assert.False(feedback.IsToastVisible);
    }

    [Fact]
    public async Task ShowToast_auto_hides_after_delay()
    {
        var tcs = new TaskCompletionSource();
        var feedback = new ShellStatusFeedback
        {
            DelayAsync = async (_, ct) =>
            {
                await tcs.Task.WaitAsync(ct);
            }
        };

        feedback.ShowToast("done", ShellToastKind.Success);
        Assert.True(feedback.IsToastVisible);

        tcs.SetResult();
        await WaitUntilAsync(() => !feedback.IsToastVisible);

        Assert.False(feedback.IsToastVisible);
    }

    [Theory]
    [InlineData(ShellToastKind.Info, 2400)]
    [InlineData(ShellToastKind.Success, 2400)]
    [InlineData(ShellToastKind.Error, 4000)]
    public void GetToastHideDelayMs_matches_kind(ShellToastKind kind, int expectedMs)
    {
        Assert.Equal(expectedMs, ShellStatusFeedback.GetToastHideDelayMs(kind));
    }

    [Fact]
    public void SetComposerStatus_shows_and_clears_sticky_strip()
    {
        var feedback = new ShellStatusFeedback();

        feedback.SetComposerStatus(" listening ");
        Assert.True(feedback.IsComposerStatusVisible);
        Assert.Equal("listening", feedback.ComposerStatusText);

        feedback.SetComposerStatus(null);
        Assert.False(feedback.IsComposerStatusVisible);
        Assert.Equal(string.Empty, feedback.ComposerStatusText);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }
}

public sealed class ContextOccupancyCompactCtaTests
{
    [Theory]
    [InlineData(ContextPressureLevel.Normal, false)]
    [InlineData(ContextPressureLevel.Elevated, false)]
    [InlineData(ContextPressureLevel.High, false)]
    [InlineData(ContextPressureLevel.Critical, true)]
    [InlineData(ContextPressureLevel.Overflow, true)]
    public void IsCompactCtaEmphasized_tracks_pressure(ContextPressureLevel pressure, bool expected)
    {
        var occupancy = new ContextOccupancyViewModel();
        var budget = new ContextBudgetSnapshot(100_000, 1_000, 1_000, 90_000, 50_000, 0.5);

        occupancy.Apply(budget, pressure);

        Assert.Equal(expected, occupancy.IsCompactCtaEmphasized);
    }

    [Fact]
    public void CompactCommand_CanExecute_follows_busy_and_compacting_gates()
    {
        var messageCount = 1;
        var isBusy = false;
        var isCompacting = false;
        var occupancy = new ContextOccupancyViewModel
        {
            CompactCommand = new RelayCommand(
                () => { },
                () => messageCount > 0 && !isBusy && !isCompacting)
        };

        Assert.NotNull(occupancy.CompactCommand);
        Assert.True(occupancy.CompactCommand.CanExecute(null));

        isBusy = true;
        occupancy.CompactCommand.NotifyCanExecuteChanged();
        Assert.False(occupancy.CompactCommand.CanExecute(null));

        isBusy = false;
        isCompacting = true;
        occupancy.IsCompacting = true;
        occupancy.CompactCommand.NotifyCanExecuteChanged();
        Assert.False(occupancy.CompactCommand.CanExecute(null));
        Assert.True(occupancy.IsCompacting);

        isCompacting = false;
        messageCount = 0;
        occupancy.IsCompacting = false;
        occupancy.CompactCommand.NotifyCanExecuteChanged();
        Assert.False(occupancy.CompactCommand.CanExecute(null));
    }
}
