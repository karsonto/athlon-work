using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ScheduleTimingTests
{
    [Fact]
    public void ComputeNextDaily_WhenTimeNotYetReachedToday_ReturnsToday()
    {
        var utcNow = new DateTime(2025, 6, 11, 1, 0, 0, DateTimeKind.Utc);
        var task = new ScheduledTask { TimeOfDay = "09:00" };

        var next = ScheduleTiming.ComputeNextDaily(task, utcNow);

        Assert.True(DateTime.TryParse(next, out var parsed));
        var local = parsed.ToLocalTime();
        Assert.Equal(9, local.Hour);
        Assert.Equal(0, local.Minute);
        Assert.Equal(utcNow.ToLocalTime().Date, local.Date);
    }

    [Fact]
    public void ComputeNextDaily_WhenTimeAlreadyPassedToday_ReturnsTomorrow()
    {
        var utcNow = new DateTime(2025, 6, 11, 14, 0, 0, DateTimeKind.Utc);
        var task = new ScheduledTask { TimeOfDay = "09:00" };

        var next = ScheduleTiming.ComputeNextDaily(task, utcNow);

        Assert.True(DateTime.TryParse(next, out var parsed));
        var local = parsed.ToLocalTime();
        Assert.Equal(utcNow.ToLocalTime().Date.AddDays(1), local.Date);
    }

    [Fact]
    public void ComputeNextRun_Interval_AddsMinutesFromNow()
    {
        var utcNow = new DateTime(2025, 6, 11, 10, 0, 0, DateTimeKind.Utc);
        var task = new ScheduledTask { Kind = "interval", EveryMinutes = 30 };

        var next = ScheduleTiming.ComputeNextRun(task, utcNow);

        Assert.Equal(utcNow.AddMinutes(30), DateTime.Parse(next).ToUniversalTime());
    }

    [Fact]
    public void ComputeNextRun_Manual_ReturnsEmpty()
    {
        var task = new ScheduledTask { Kind = "manual" };

        Assert.Equal("", ScheduleTiming.ComputeNextRun(task));
    }

    [Fact]
    public void IsDue_WhenNextRunAtInPast_ReturnsTrue()
    {
        var utcNow = new DateTime(2025, 6, 11, 12, 0, 0, DateTimeKind.Utc);
        var task = new ScheduledTask
        {
            Kind = "daily",
            NextRunAt = utcNow.AddMinutes(-5).ToString("O")
        };

        Assert.True(ScheduleTiming.IsDue(task, utcNow));
    }

    [Fact]
    public void IsDue_WhenNextRunAtInFuture_ReturnsFalse()
    {
        var utcNow = new DateTime(2025, 6, 11, 12, 0, 0, DateTimeKind.Utc);
        var task = new ScheduledTask
        {
            Kind = "daily",
            NextRunAt = utcNow.AddHours(1).ToString("O")
        };

        Assert.False(ScheduleTiming.IsDue(task, utcNow));
    }

    [Fact]
    public void BuildPrompt_PrependsPrefixWhenConfigured()
    {
        var task = new ScheduledTask { Prompt = "do work" };
        var schedule = new ScheduleSettings { PromptPrefix = "You are a helper." };

        Assert.Equal("You are a helper.\ndo work", ScheduleTiming.BuildPrompt(task, schedule));
    }

    [Fact]
    public void ResolveWorkspaceRoot_UsesTaskWorkspaceOnly()
    {
        var task = new ScheduledTask { WorkspaceRoot = @"C:\work" };

        Assert.Equal(@"C:\work", ScheduleTiming.ResolveWorkspaceRoot(task));
    }

    [Fact]
    public void ResolveModeOptions_Ask_DisablesToolCalls()
    {
        var (allowToolCalls, maxRounds) = ScheduleTiming.ResolveModeOptions("ask");

        Assert.False(allowToolCalls);
        Assert.Null(maxRounds);
    }

    [Fact]
    public void ResolveModeOptions_Plan_LimitsToolRounds()
    {
        var (allowToolCalls, maxRounds) = ScheduleTiming.ResolveModeOptions("plan");

        Assert.True(allowToolCalls);
        Assert.Equal(ScheduleTiming.PlanModeMaxToolRounds, maxRounds);
    }
}
