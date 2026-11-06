using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Tests.Teams;

public sealed class TeamToolDefaultsTests
{
    [Fact]
    public void ResolveAllowList_WorkerDefault_ExcludesTeamTools()
    {
        var profile = new AgentProfile { Id = "w", Name = "W" };
        var list = TeamToolDefaults.ResolveAllowList(profile, isLeader: false);
        Assert.Contains("file_read", list);
        Assert.DoesNotContain("team_dispatch", list);
    }

    [Fact]
    public void ResolveAllowList_LeaderDefault_IncludesTeamTools()
    {
        var profile = new AgentProfile { Id = "l", Name = "L" };
        var list = TeamToolDefaults.ResolveAllowList(profile, isLeader: true);
        Assert.Contains("team_dispatch", list);
        Assert.Contains("file_read", list);
    }

    [Fact]
    public void MatchesAllowList_SupportsPrefixWildcard()
    {
        Assert.True(TeamToolDefaults.MatchesAllowList("team_dispatch", ["team_*"]));
        Assert.False(TeamToolDefaults.MatchesAllowList("sessions_spawn", ["team_*"]));
    }
}
