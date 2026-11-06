using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Tests.Teams;

public sealed class AgentProfileTurnScopeTests
{
    [Fact]
    public void IsMcpServerAllowed_UsesProfileFilter_WhenEntered()
    {
        using var scope = AgentProfileTurnScope.Enter(new AgentProfileTurnOptions(
            ProfileId: "p1",
            McpServerNames: ["github"]));

        Assert.True(AgentProfileTurnScope.IsMcpServerAllowed("github"));
        Assert.False(AgentProfileTurnScope.IsMcpServerAllowed("jira"));
    }

    [Fact]
    public void IsMcpServerAllowed_EmptyList_DeniesAll()
    {
        using var scope = AgentProfileTurnScope.Enter(new AgentProfileTurnOptions(
            ProfileId: "p1",
            McpServerNames: Array.Empty<string>()));

        Assert.False(AgentProfileTurnScope.IsMcpServerAllowed("github"));
        Assert.False(AgentProfileTurnScope.IsMcpServerAllowed("anything"));
    }

    [Fact]
    public void SkillNames_EmptyList_IsExposedForDenySemantics()
    {
        using var scope = AgentProfileTurnScope.Enter(new AgentProfileTurnOptions(
            ProfileId: "p1",
            SkillNames: Array.Empty<string>()));

        Assert.NotNull(AgentProfileTurnScope.SkillNames);
        Assert.Empty(AgentProfileTurnScope.SkillNames!);
    }

    [Fact]
    public void IsMcpServerAllowed_FallsBackToSchedule_WhenNoProfileScope()
    {
        Assert.Null(AgentProfileTurnScope.Current);
        using var schedule = ScheduleTurnScope.Enter(new ScheduleTurnOptions(McpServerNames: ["only-a"]));
        Assert.True(AgentProfileTurnScope.IsMcpServerAllowed("only-a"));
        Assert.False(AgentProfileTurnScope.IsMcpServerAllowed("other"));
    }

    [Fact]
    public void SkillNames_ExposedFromAmbient()
    {
        using var scope = AgentProfileTurnScope.Enter(new AgentProfileTurnOptions(
            ProfileId: "p1",
            SkillNames: ["release-notes"]));
        Assert.NotNull(AgentProfileTurnScope.SkillNames);
        Assert.Contains("release-notes", AgentProfileTurnScope.SkillNames!);
    }
}
