using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class AgentRecordGroupingTests
{
    [Fact]
    public void Build_groups_sessions_by_repository_workspace()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            new SessionIndexEntry("s1", "Chat A", "/sessions/s1", now, 1, @"F:\athlon-work"),
            new SessionIndexEntry("s2", "Chat B", "/sessions/s2", now.AddMinutes(-5), 2, @"F:\athlon-work"),
            new SessionIndexEntry("s3", "Other", "/sessions/s3", now.AddHours(-1), 1, @"D:\repos\OpenHarness"),
            new SessionIndexEntry("s4", "Loose", "/sessions/s4", now.AddDays(-1), 0, null)
        };

        var groups = AgentRecordGrouping.Build(entries, "s1", _ => false, null);

        Assert.Equal(3, groups.Count);
        var noWorkspace = Assert.Single(groups, group => group.Key == AgentRecordGrouping.NoWorkspaceKey);
        Assert.Single(noWorkspace.Items);
        Assert.Equal("s4", noWorkspace.Items[0].Id);

        var athlon = Assert.Single(groups, group => group.Title == "athlon-work");
        Assert.Equal(2, athlon.Items.Count);
        Assert.Equal("s1", athlon.Items[0].Id);
        Assert.True(athlon.IsExpanded);

        var openHarness = Assert.Single(groups, group => group.Title == "OpenHarness");
        Assert.Single(openHarness.Items);
        Assert.False(openHarness.IsExpanded);
    }

    [Fact]
    public void Build_preserves_expanded_keys_across_rebuild()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            new SessionIndexEntry("a", "A", "/a", now, null, @"F:\athlon-work"),
            new SessionIndexEntry("b", "B", "/b", now, null, @"D:\OpenHarness")
        };
        var openHarnessKey = AgentRecordGrouping.ResolveRepositoryKey(@"D:\OpenHarness");

        var groups = AgentRecordGrouping.Build(
            entries,
            activeSessionId: "missing",
            _ => false,
            null,
            previouslyExpandedKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { openHarnessKey });

        Assert.True(groups.Single(group => group.Title == "OpenHarness").IsExpanded);
        Assert.False(groups.Single(group => group.Title == "athlon-work").IsExpanded);
    }

    [Fact]
    public void FormatRelativeTime_uses_compact_units()
    {
        var now = AppTimeZone.Now;
        Assert.Equal("now", SessionHistoryItemViewModel.FormatRelativeTime(now));
        Assert.Equal("5m", SessionHistoryItemViewModel.FormatRelativeTime(now.AddMinutes(-5)));
        Assert.Equal("2h", SessionHistoryItemViewModel.FormatRelativeTime(now.AddHours(-2)));
        Assert.Equal("3d", SessionHistoryItemViewModel.FormatRelativeTime(now.AddDays(-3)));
    }
}
