using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ScheduleTimingTests
{
    [Fact]
    public void ComputeNextDaily_WhenTimeNotYetReachedToday_ReturnsToday()
    {
        var utcNow = new DateTime(2025, 6, 11, 0, 30, 0, DateTimeKind.Utc);
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
        var schedule = new ScheduleSettings { DefaultWorkspaceRoot = @"C:\default" };

        Assert.Equal(@"C:\work", ScheduleTiming.ResolveWorkspaceRoot(task, schedule));
    }

    [Fact]
    public void ResolveWorkspaceRoot_FallsBackToScheduleDefault()
    {
        var task = new ScheduledTask { WorkspaceRoot = "" };
        var schedule = new ScheduleSettings { DefaultWorkspaceRoot = @"C:\default" };

        Assert.Equal(@"C:\default", ScheduleTiming.ResolveWorkspaceRoot(task, schedule));
    }

    [Fact]
    public void ResolveAllowList_EmptyTaskList_ReturnsNull()
    {
        Assert.Null(ScheduleTiming.ResolveAllowList([], ["a", "b"]));
    }

    [Fact]
    public void ResolveAllowList_IntersectsWithGlobal()
    {
        var resolved = ScheduleTiming.ResolveAllowList(["a", "missing"], ["a", "b"]);

        Assert.NotNull(resolved);
        Assert.Equal(["a"], resolved);
    }

    [Fact]
    public void ResolveModeOptions_Ask_DisablesToolCalls()
    {
        var (allowToolCalls, maxRounds) = ScheduleTiming.ResolveModeOptions("ask");

        Assert.False(allowToolCalls);
        Assert.Null(maxRounds);
    }

    [Fact]
    public void ResolveModeOptions_Agent_AllowsToolCallsWithoutRoundLimit()
    {
        var (allowToolCalls, maxRounds) = ScheduleTiming.ResolveModeOptions("agent");

        Assert.True(allowToolCalls);
        Assert.Null(maxRounds);
    }

    [Fact]
    public void IsConsumedOneShot_TrueOnlyWhenDisabledAndFired()
    {
        var consumed = new ScheduledTask
        {
            Kind = "at",
            AtTime = "2025-06-11 10:00",
            Enabled = false,
            LastRunAt = "2025-06-11T02:00:00.000Z"
        };

        Assert.True(ScheduleTiming.IsConsumedOneShot(consumed));
    }

    [Fact]
    public void IsConsumedOneShot_FalseForUserDisabledNeverRun()
    {
        // The user switched it off before it ever fired: not "completed".
        var disabled = new ScheduledTask
        {
            Kind = "at",
            AtTime = "2099-06-11 10:00",
            Enabled = false,
            LastRunAt = ""
        };

        Assert.False(ScheduleTiming.IsConsumedOneShot(disabled));
    }

    [Fact]
    public void IsConsumedOneShot_FalseForOtherKinds()
    {
        var daily = new ScheduledTask
        {
            Kind = "daily",
            Enabled = false,
            LastRunAt = "2025-06-11T02:00:00.000Z"
        };

        Assert.False(ScheduleTiming.IsConsumedOneShot(daily));
    }

    [Fact]
    public void ComputeNextRun_OneShotPast_ReturnsEmpty()
    {
        var utcNow = new DateTime(2025, 6, 11, 12, 0, 0, DateTimeKind.Utc);
        var task = new ScheduledTask
        {
            Kind = "at",
            AtTime = utcNow.ToLocalTime().AddMinutes(-5).ToString(ScheduleTiming.AtTimeFormat)
        };

        Assert.Equal("", ScheduleTiming.ComputeNextRun(task, utcNow));
    }

    [Fact]
    public void ComputeNextRun_OneShotFuture_ReturnsScheduledMoment()
    {
        var utcNow = new DateTime(2025, 6, 11, 12, 0, 0, DateTimeKind.Utc);
        var localFuture = utcNow.ToLocalTime().AddHours(2);
        var task = new ScheduledTask
        {
            Kind = "at",
            AtTime = localFuture.ToString(ScheduleTiming.AtTimeFormat)
        };

        var next = ScheduleTiming.ComputeNextRun(task, utcNow);

        Assert.False(string.IsNullOrEmpty(next));
        Assert.Equal(
            DateTime.ParseExact(localFuture.ToString(ScheduleTiming.AtTimeFormat), ScheduleTiming.AtTimeFormat, null)
                .ToUniversalTime(),
            DateTime.Parse(next).ToUniversalTime());
    }

    [Fact]
    public void ShouldRunImmediately_OneShotPastWithLastRun_StillTrueForRetry()
    {
        // Re-enabling a consumed one-shot means "run it again": it must fire once more, then be
        // auto-disabled again. This is why ShouldRunImmediately keeps no LastRunAt guard.
        var task = new ScheduledTask
        {
            Kind = "at",
            Enabled = true,
            AtTime = DateTime.UtcNow.ToLocalTime().AddMinutes(-30).ToString(ScheduleTiming.AtTimeFormat),
            LastRunAt = "2025-06-11T02:00:00.000Z"
        };

        Assert.True(ScheduleTiming.ShouldRunImmediately(task));
    }

    [Fact]
    public void IsOneShotScheduledInFuture_TrueForFutureMomentAndFalseForPast()
    {
        var utcNow = new DateTime(2025, 6, 11, 12, 0, 0, DateTimeKind.Utc);

        var future = new ScheduledTask
        {
            Kind = "at",
            AtTime = utcNow.ToLocalTime().AddHours(1).ToString(ScheduleTiming.AtTimeFormat)
        };
        var past = new ScheduledTask
        {
            Kind = "at",
            AtTime = utcNow.ToLocalTime().AddHours(-1).ToString(ScheduleTiming.AtTimeFormat)
        };

        Assert.True(ScheduleTiming.IsOneShotScheduledInFuture(future, utcNow));
        Assert.False(ScheduleTiming.IsOneShotScheduledInFuture(past, utcNow));
    }

    [Fact]
    public void TryParseAtTime_UsesExactFormatIndependentOfCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // A culture whose short date pattern is day-first would misread a month-first string.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            Assert.True(ScheduleTiming.TryParseAtTime("2025-12-31 18:00", out var parsed));
            Assert.Equal(2025, parsed.Year);
            Assert.Equal(12, parsed.Month);
            Assert.Equal(31, parsed.Day);
            Assert.Equal(18, parsed.Hour);
            Assert.Equal(0, parsed.Minute);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TryParseAtTime_FallsBackToLenientParseForLegacyValues()
    {
        Assert.True(ScheduleTiming.TryParseAtTime("2025/12/31 18:00", out var parsed));
        Assert.Equal(2025, parsed.Year);
        Assert.Equal(31, parsed.Day);
    }

    [Fact]
    public void TryParseAtTime_ReturnsFalseForBlankOrInvalid()
    {
        Assert.False(ScheduleTiming.TryParseAtTime("", out _));
        Assert.False(ScheduleTiming.TryParseAtTime("   ", out _));
        Assert.False(ScheduleTiming.TryParseAtTime("not-a-date", out _));
    }
}
