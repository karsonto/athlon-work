using Athlon.Agent.Core;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.Tests;

public sealed class AgentRecordGroupingTests
{
    [Fact]
    public void Build_GroupsByTodayAndLast7Days()
    {
        var now = DateTimeOffset.Now;
        var entries = new[]
        {
            new SessionIndexEntry("today", "Today", "/a", now),
            new SessionIndexEntry("yesterday", "Yesterday", "/b", now.AddDays(-1)),
            new SessionIndexEntry("old", "Old", "/c", now.AddDays(-10))
        };

        var groups = AgentRecordGrouping.Build(entries, "other", _ => false, null);
        var today = groups.First(g => g.Key == AgentRecordGrouping.TodayKey);
        var last7 = groups.First(g => g.Key == AgentRecordGrouping.Last7DaysKey);
        var earlier = groups.First(g => g.Key == AgentRecordGrouping.EarlierKey);

        Assert.Single(today.Items);
        Assert.Equal("today", today.Items[0].Id);
        Assert.Single(last7.Items);
        Assert.Equal("yesterday", last7.Items[0].Id);
        Assert.Single(earlier.Items);
        Assert.Equal("old", earlier.Items[0].Id);
    }
}
