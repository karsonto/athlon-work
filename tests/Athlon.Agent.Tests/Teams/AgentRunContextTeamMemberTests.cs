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
}
