using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Infrastructure.Teams;

namespace Athlon.Agent.Tests.Teams;

public sealed class TeamToolDefaultsTests
{
    [Fact]
    public void ResolveAllowList_WorkerDefault_IncludesSharedTeamTools_ExcludesLeaderTools()
    {
        var profile = new AgentProfile { Id = "w", Name = "W" };
        var list = TeamToolDefaults.ResolveAllowList(profile, isLeader: false);
        Assert.Contains("file_read", list);
        Assert.Contains("team_progress", list);
        Assert.Contains("team_memory", list);
        Assert.DoesNotContain(TeamToolDefaults.LoadSkillThroughPathTool, list);
        Assert.DoesNotContain("team_dispatch", list);
        Assert.DoesNotContain("team_submit_result", list);
    }

    [Fact]
    public void ResolveAllowList_LeaderDefault_IncludesTeamTools_ExcludesFileWrite()
    {
        var profile = new AgentProfile { Id = "l", Name = "L" };
        var list = TeamToolDefaults.ResolveAllowList(profile, isLeader: true);
        Assert.Contains("team_dispatch", list);
        Assert.Contains("team_progress", list);
        Assert.Contains("team_memory", list);
        Assert.DoesNotContain("file_write", list);
        Assert.DoesNotContain("file_read", list);
    }

    [Fact]
    public void DefaultLeaderAllowList_IsOrchestrationOnly()
    {
        var list = TeamToolDefaults.DefaultLeaderAllowList;
        Assert.All(list, name => Assert.StartsWith("team_", name));
        Assert.DoesNotContain(list, name => name.StartsWith("file_", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("team_submit_result", list);
    }

    [Fact]
    public void MatchesAllowList_SupportsPrefixWildcard()
    {
        Assert.True(TeamToolDefaults.MatchesAllowList("team_dispatch", ["team_*"]));
        Assert.False(TeamToolDefaults.MatchesAllowList("sessions_spawn", ["team_*"]));
    }

    [Fact]
    public void ResolveAllowList_Worker_StripsPersistedLeaderWildcard()
    {
        var profile = new AgentProfile
        {
            Id = "w",
            Name = "W",
            ToolAllowList = ["file_read", "team_progress", "team_memory", "team_*", "team_dispatch"]
        };
        var list = TeamToolDefaults.ResolveAllowList(profile, isLeader: false);
        Assert.Contains("file_read", list);
        Assert.Contains("team_progress", list);
        Assert.Contains("team_memory", list);
        Assert.DoesNotContain("team_*", list);
        Assert.DoesNotContain("team_dispatch", list);
    }

    [Fact]
    public void ResolveAllowList_Worker_AlwaysInjectsSharedTeamTools()
    {
        var profile = new AgentProfile
        {
            Id = "w",
            Name = "W",
            ToolAllowList = ["file_read", "grep_files"]
        };
        var list = TeamToolDefaults.ResolveAllowList(profile, isLeader: false);
        Assert.Contains("file_read", list);
        Assert.Contains("grep_files", list);
        Assert.Contains("team_progress", list);
        Assert.Contains("team_memory", list);
        Assert.DoesNotContain(TeamToolDefaults.LoadSkillThroughPathTool, list);
        Assert.DoesNotContain(TeamToolDefaults.KnowledgeSearchTool, list);
    }

    [Fact]
    public void ResolveAllowList_Worker_WithSkills_InjectsLoadSkillTool()
    {
        var profile = new AgentProfile
        {
            Id = "w",
            Name = "W",
            ToolAllowList = ["file_read"],
            SkillNames = ["git-summary"]
        };
        var list = TeamToolDefaults.ResolveAllowList(profile, isLeader: false);
        Assert.Contains("file_read", list);
        Assert.Contains(TeamToolDefaults.LoadSkillThroughPathTool, list);
        Assert.Contains("team_progress", list);
    }

    [Fact]
    public void ResolveAllowList_Worker_WithoutSkills_StripsPersistedLoadSkillTool()
    {
        var profile = new AgentProfile
        {
            Id = "w",
            Name = "W",
            ToolAllowList = ["file_read", TeamToolDefaults.LoadSkillThroughPathTool],
            SkillNames = []
        };
        var list = TeamToolDefaults.ResolveAllowList(profile, isLeader: false);
        Assert.Contains("file_read", list);
        Assert.DoesNotContain(TeamToolDefaults.LoadSkillThroughPathTool, list);
    }

    [Fact]
    public void ResolveAllowList_Worker_WithKnowledge_InjectsKnowledgeSearchTool()
    {
        var profile = new AgentProfile
        {
            Id = "w",
            Name = "W",
            ToolAllowList = ["file_read"],
            KnowledgeModuleIds = ["kb-space-1"]
        };
        var list = TeamToolDefaults.ResolveAllowList(profile, isLeader: false);
        Assert.Contains(TeamToolDefaults.KnowledgeSearchTool, list);
        Assert.DoesNotContain(TeamToolDefaults.LoadSkillThroughPathTool, list);
    }

    [Fact]
    public void ResolveAllowList_Worker_WithoutKnowledge_StripsPersistedKnowledgeSearchTool()
    {
        var profile = new AgentProfile
        {
            Id = "w",
            Name = "W",
            ToolAllowList = ["file_read", TeamToolDefaults.KnowledgeSearchTool],
            KnowledgeModuleIds = []
        };
        var list = TeamToolDefaults.ResolveAllowList(profile, isLeader: false);
        Assert.DoesNotContain(TeamToolDefaults.KnowledgeSearchTool, list);
    }

    [Fact]
    public void IsLeaderOrchestrationEntry_ExcludesSharedProgressMemory()
    {
        Assert.True(TeamToolDefaults.IsLeaderOrchestrationEntry("team_*"));
        Assert.True(TeamToolDefaults.IsLeaderOrchestrationEntry("team_dispatch"));
        Assert.False(TeamToolDefaults.IsLeaderOrchestrationEntry("team_progress"));
        Assert.False(TeamToolDefaults.IsLeaderOrchestrationEntry("team_memory"));
    }

    [Fact]
    public void ProfileToolRouter_LeaderKeepsExcludedTeamLeaderTools()
    {
        var tools = new IAgentTool[]
        {
            new StubTeamLeaderTool(),
            new StubWorkerTool(),
            new StubSharedTeamTool()
        };

        var allow = new[] { "team_*", "file_read" };
        var leaderNames = ProfileToolRouterFilter.Filter(tools, allow, isLeader: true)
            .Select(t => t.Definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("team_dispatch", leaderNames);
        Assert.Contains("file_read", leaderNames);
        Assert.Contains("team_progress", leaderNames);

        var workerNames = ProfileToolRouterFilter.Filter(tools, allow, isLeader: false)
            .Select(t => t.Definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("team_dispatch", workerNames);
        Assert.Contains("file_read", workerNames);
        Assert.Contains("team_progress", workerNames);
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

    private sealed class StubSharedTeamTool : IAgentTool
    {
        public ToolDefinition Definition => new("team_progress", "d", ToolSchema.Object().Build());

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
