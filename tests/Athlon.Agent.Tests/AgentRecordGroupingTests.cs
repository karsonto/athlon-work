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
        Assert.False(athlon.IsRemote);
        Assert.Equal("\uE838", athlon.FolderGlyph);

        var openHarness = Assert.Single(groups, group => group.Title == "OpenHarness");
        Assert.Single(openHarness.Items);
        Assert.False(openHarness.IsExpanded);
        Assert.False(openHarness.IsRemote);
        Assert.Equal("\uE8B7", openHarness.FolderGlyph);
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
    public void Build_marks_remote_workspace_with_cloud_glyph()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            new SessionIndexEntry(
                "remote-1",
                "SSH Chat",
                "/sessions/remote-1",
                now,
                2,
                "/home/user/athlon-work",
                "ssh-workspace-1")
        };

        var groups = AgentRecordGrouping.Build(entries, "remote-1", _ => false, null);

        var remote = Assert.Single(groups);
        Assert.True(remote.IsRemote);
        Assert.Equal("athlon-work", remote.Title);
        Assert.Equal("ssh-workspace-1", remote.ActiveWorkspaceId);
        Assert.Equal("\uE753", remote.FolderGlyph);
        Assert.StartsWith("ssh:ssh-workspace-1:", remote.Key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_keeps_local_and_remote_same_name_in_separate_groups()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            new SessionIndexEntry("local", "Local", "/sessions/local", now, 1, @"F:\repos\demo"),
            new SessionIndexEntry(
                "remote",
                "Remote",
                "/sessions/remote",
                now.AddMinutes(-1),
                1,
                "/home/user/demo",
                "ssh-demo")
        };

        var groups = AgentRecordGrouping.Build(entries, "local", _ => false, null);

        Assert.Equal(2, groups.Count);
        var local = Assert.Single(groups, group => !group.IsRemote);
        var remote = Assert.Single(groups, group => group.IsRemote);
        Assert.Equal("demo", local.Title);
        Assert.Equal("demo", remote.Title);
        Assert.NotEqual(local.Key, remote.Key, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("\uE838", local.FolderGlyph);
        Assert.Equal("\uE753", remote.FolderGlyph);
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
