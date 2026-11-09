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

    [Fact]
    public void Parse_BoldStatusLine_Succeeds()
    {
        var parsed = TeamMemberResultParser.Parse("""
            ## Result
            **status:** completed
            accomplished:
            - extracted ToolResultDiffExtractor
            """);
        Assert.Equal(TeamMemberResultStatus.Completed, parsed.Status);
        Assert.True(TeamMemberResultParser.IsSuccessful(parsed));
    }

    [Fact]
    public void Parse_FullwidthColonStatus_Succeeds()
    {
        var parsed = TeamMemberResultParser.Parse("""
            ## Result
            status：completed
            findings:
            - none
            """);
        Assert.Equal(TeamMemberResultStatus.Completed, parsed.Status);
    }

    [Fact]
    public void Parse_MidTurnBuildProgress_IsInvalid()
    {
        var parsed = TeamMemberResultParser.Parse(
            "Targeted tests: **18 passed / 0 failed** → R1-01 done. Recording and moving to R1-02 (reading source regions).");
        Assert.Equal(TeamMemberResultStatus.Invalid, parsed.Status);
    }

    [Fact]
    public void Parse_LongCompletedProseWithoutStatus_IsInvalid()
    {
        var body = string.Concat(
            "本轮配方已应用完毕，已完成两处 ExtractClass。",
            " changed_files: A.cs, B.cs。",
            new string('x', 120),
            " applied: R1-01, R1-02。验收：编译 0 errors。");
        var parsed = TeamMemberResultParser.Parse(body);
        Assert.Equal(TeamMemberResultStatus.Invalid, parsed.Status);
    }

    [Fact]
    public void Parse_ResultHeadingWithoutStatus_IsInvalid()
    {
        var parsed = TeamMemberResultParser.Parse("""
            ## Result
            accomplished:
            - did a lot of work without a status line
            findings:
            - none
            """ + new string('y', 200));
        Assert.Equal(TeamMemberResultStatus.Invalid, parsed.Status);
    }
}
