using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Tests.Teams;

public sealed class AgentRunContextTeamMemberTests
{
    [Fact]
    public void CreateTeamMember_SetsKindAndDirectory()
    {
        var root = AgentRunContext.CreateRoot(
            AgentSession.Create("parent") with { Id = "parent-1" },
            "run-root",
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            WorkspaceIgnoreDefaults.BuiltIn);

        var child = root.CreateTeamMember(
            "mbr_1",
            "tm_abc",
            "profile-qa",
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            "QA",
            new AgentLoopOptions { MaxModelToolRounds = 8 },
            workspaceRoot: null,
            ignorePatterns: WorkspaceIgnoreDefaults.BuiltIn);

        Assert.Equal(AgentRunKind.TeamMember, child.Kind);
        Assert.Equal("tm_abc", child.TeamTaskId);
        Assert.Equal("profile-qa", child.TeamProfileId);
        Assert.Equal("parent-1", child.ParentSessionId);

        var dir = child.ResolveSessionDirectory(Path.Combine(Path.GetTempPath(), "sessions"), "mbr_1");
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "sessions", "parent-1", "teams", "tm_abc", "members", "mbr_1"),
            dir);
    }

    [Fact]
    public void CreateTeamMember_Leader_UsesStableLeadersDirectory()
    {
        var root = AgentRunContext.CreateRoot(
            AgentSession.Create("parent") with { Id = "parent-1" },
            "run-root",
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            WorkspaceIgnoreDefaults.BuiltIn);

        var leader = root.CreateTeamMember(
            "ldr_t1",
            "tm_abc",
            "profile-leader",
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            "Leader",
            new AgentLoopOptions { MaxModelToolRounds = 8 },
            workspaceRoot: null,
            ignorePatterns: WorkspaceIgnoreDefaults.BuiltIn,
            teamId: "t1",
            isLeader: true);

        Assert.True(leader.IsTeamLeader);
        Assert.Equal("t1", leader.TeamId);
        var dir = leader.ResolveSessionDirectory(Path.Combine(Path.GetTempPath(), "sessions"), "ldr_t1");
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "sessions", "parent-1", "teams", "_leaders", "t1"),
            dir);
    }
}
