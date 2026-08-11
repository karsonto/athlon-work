using Athlon.Agent.App.Services.ComputerUse;

namespace Athlon.Agent.Tests;

public sealed class ComputerUseFrameFreshnessTests
{
    [Fact]
    public void IsWithinAge_AllowsTwoMinutes()
    {
        var created = DateTimeOffset.Parse("2026-08-11T12:00:00Z");
        Assert.True(ComputerUseFrameFreshness.IsWithinAge(
            created,
            created.AddMinutes(2)));
        Assert.False(ComputerUseFrameFreshness.IsWithinAge(
            created,
            created.AddMinutes(2).AddSeconds(1)));
    }

    [Fact]
    public void MatchesMonitor_RequiresExactBounds()
    {
        Assert.True(ComputerUseFrameFreshness.MatchesMonitor(0, 0, 1920, 1080, 0, 0, 1920, 1080));
        Assert.False(ComputerUseFrameFreshness.MatchesMonitor(0, 0, 1920, 1080, 1920, 0, 1920, 1080));
    }

    [Fact]
    public void MatchesForegroundProcess_IgnoresCase_AndDoesNotRequireTitle()
    {
        Assert.True(ComputerUseFrameFreshness.MatchesForegroundProcess("explorer", "Explorer"));
        Assert.False(ComputerUseFrameFreshness.MatchesForegroundProcess("explorer", "chrome"));
    }

    [Fact]
    public void ContainsPoint_UsesHalfOpenMonitorBounds()
    {
        Assert.True(ComputerUseFrameFreshness.ContainsPoint(0, 0, 100, 100, 0, 0));
        Assert.True(ComputerUseFrameFreshness.ContainsPoint(0, 0, 100, 100, 99, 99));
        Assert.False(ComputerUseFrameFreshness.ContainsPoint(0, 0, 100, 100, 100, 50));
        Assert.False(ComputerUseFrameFreshness.ContainsPoint(0, 0, 100, 100, -1, 10));
    }
}
