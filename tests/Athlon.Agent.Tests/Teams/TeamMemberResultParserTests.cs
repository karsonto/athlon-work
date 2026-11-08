using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Tests.Teams;

public sealed class TeamMemberResultParserTests
{
    [Fact]
    public void Parse_CompletedStatus_Succeeds()
    {
        var parsed = TeamMemberResultParser.Parse("""
            ## Result
            status: completed
            accomplished:
            - did the work
            """);
        Assert.Equal(TeamMemberResultStatus.Completed, parsed.Status);
        Assert.True(TeamMemberResultParser.IsSuccessful(parsed));
    }

    [Fact]
    public void Parse_VerdictFail_IsFailed()
    {
        var parsed = TeamMemberResultParser.Parse("""
            ## Result
            status: failed
            verdict: fail
            fix_target_subtask_id: sub_abc
            findings:
            - missing tests
            """);
        Assert.Equal(TeamMemberResultStatus.Failed, parsed.Status);
        Assert.Equal("fail", parsed.Verdict);
        Assert.Equal("sub_abc", parsed.FixTargetSubtaskId);
        Assert.False(TeamMemberResultParser.IsSuccessful(parsed));
        Assert.Contains("missing tests", parsed.FindingsSummary);
    }

    [Fact]
    public void Parse_PreambleOnly_IsInvalid()
    {
        var parsed = TeamMemberResultParser.Parse("Let me open a terminal to get exact line counts.");
        Assert.Equal(TeamMemberResultStatus.Invalid, parsed.Status);
    }

    [Fact]
    public void Parse_StartByInspectingPreamble_IsInvalid()
    {
        var parsed = TeamMemberResultParser.Parse(
            "I'll start by inspecting the shared task state and workspace structure.");
        Assert.Equal(TeamMemberResultStatus.Invalid, parsed.Status);
        Assert.False(TeamMemberResultParser.IsSuccessful(parsed));
    }

    [Fact]
    public void Parse_ShortProseWithoutStatus_IsInvalid()
    {
        var parsed = TeamMemberResultParser.Parse("Architecture looks fine overall.");
        Assert.Equal(TeamMemberResultStatus.Invalid, parsed.Status);
    }

    [Fact]
    public void Parse_Empty_IsInvalid()
    {
        var parsed = TeamMemberResultParser.Parse("  ");
        Assert.Equal(TeamMemberResultStatus.Invalid, parsed.Status);
    }

    [Fact]
    public void Parse_Blocked_NotSuccessful()
    {
        var parsed = TeamMemberResultParser.Parse("""
            ## Result
            status: blocked
            accomplished:
            - waiting on secrets
            """);
        Assert.Equal(TeamMemberResultStatus.Blocked, parsed.Status);
        Assert.False(TeamMemberResultParser.IsSuccessful(parsed));
    }
}
