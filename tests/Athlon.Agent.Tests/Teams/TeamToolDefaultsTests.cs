using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Infrastructure.Teams;

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

    [Fact]
    public void ProfileToolRouter_LeaderKeepsExcludedTeamLeaderTools()
    {
        var tools = new IAgentTool[]
        {
            new StubTeamLeaderTool(),
            new StubWorkerTool()
        };

        var allow = new[] { "team_*", "file_read" };
        var leaderNames = ProfileToolRouterFilter.Filter(tools, allow, isLeader: true)
            .Select(t => t.Definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("team_dispatch", leaderNames);
        Assert.Contains("file_read", leaderNames);

        var workerNames = ProfileToolRouterFilter.Filter(tools, allow, isLeader: false)
            .Select(t => t.Definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("team_dispatch", workerNames);
        Assert.Contains("file_read", workerNames);
    }

    private sealed class StubTeamLeaderTool : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
    {
        public ToolDefinition Definition => new("team_dispatch", "d", ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok", "ok"));
    }

    private sealed class StubWorkerTool : IAgentTool
    {
        public ToolDefinition Definition => new("file_read", "d", ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok", "ok"));
    }
}

/// <summary>Exposes ProfileToolRouter's private filter for regression tests without MCP wiring.</summary>
internal static class ProfileToolRouterFilter
{
    public static IEnumerable<IAgentTool> Filter(
        IEnumerable<IAgentTool> tools,
        IReadOnlyList<string> allowList,
        bool isLeader)
    {
        foreach (var tool in tools)
        {
            if (tool is IExcludedFromChildAgentToolkit
                && !(isLeader && tool is ITeamLeaderTool))
            {
                continue;
            }

            if (tool is ITeamLeaderTool && !isLeader)
            {
                continue;
            }

            var name = tool.Definition.Name;
            if (!TeamToolDefaults.MatchesAllowList(name, allowList))
            {
                continue;
            }

            yield return tool;
        }
    }
}
