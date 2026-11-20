using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Storage;
using Athlon.Agent.Infrastructure.Teams;

namespace Athlon.Agent.Tests.Teams;

public sealed class TeamRedispatchAndSubmitGatesTests : IDisposable
{
    private readonly string _root;
    private readonly TestPathProvider _paths;
    private readonly FileTeamTaskStore _tasks;
    private readonly FileTeamTraceStore _traces;
    private readonly FileAgentProfileStore _profiles;
    private readonly FileTeamDefinitionStore _teams;
    private readonly AppSettings _settings;

    public TeamRedispatchAndSubmitGatesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "athlon-team-gates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new TestPathProvider(_root);
        _paths.EnsureCreated();
        var json = new JsonFileStore();
        _tasks = new FileTeamTaskStore(_paths, json);
        _traces = new FileTeamTraceStore(_tasks, json);
        _profiles = new FileAgentProfileStore(_paths, json);
        _teams = new FileTeamDefinitionStore(_paths, json);
        _settings = new AppSettings
        {
            Teams = new TeamsSettings
            {
                Enabled = true,
                MaxRepairRounds = 2
            }
        };
    }

    [Fact]
    public async Task Redispatch_SucceededWithoutConfirmRepair_Denied()
    {
        await SeedAsync(TeamSubTaskStatus.Succeeded);
        var tool = new TeamRedispatchTool(_settings, _tasks, _profiles, _traces);
        using var scope = TeamTaskExecutionScope.Enter(await _tasks.GetAsync("sess", "tm_1") ?? new TeamTask());
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_redispatch",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["subtask_id"] = "sub_a",
                    ["reason"] = "try again please",
                    ["handoff"] = "do it better",
                    ["acceptance_criteria"] = "same as before"
                })),
            default);
        Assert.False(result.Succeeded);
        var text = $"{result.Summary}\n{result.Error}\n{result.Content}";
        Assert.Contains("confirm_repair", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Redispatch_SucceededWithConfirmRepair_Allowed()
    {
        await SeedAsync(TeamSubTaskStatus.Succeeded);
        var tool = new TeamRedispatchTool(_settings, _tasks, _profiles, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_redispatch",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["subtask_id"] = "sub_a",
                    ["reason"] = "verification failed",
                    ["handoff"] = "fix findings",
                    ["acceptance_criteria"] = "tests pass",
                    ["confirm_repair"] = "true"
                })),
            default);
        Assert.True(result.Succeeded, result.Error ?? result.Content ?? result.Summary);
    }

    [Fact]
    public async Task Redispatch_VerifyFailReason_Allowed()
    {
        await SeedAsync(TeamSubTaskStatus.Failed);
        var tool = new TeamRedispatchTool(_settings, _tasks, _profiles, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_redispatch",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["subtask_id"] = "sub_a",
                    ["reason"] = "verification failed: missing tests",
                    ["handoff"] = "findings:\n- add unit tests",
                    ["acceptance_criteria"] = "unit tests added and green"
                })),
            default);
        Assert.True(result.Succeeded, result.Error ?? result.Content ?? result.Summary);
        var task = await _tasks.GetAsync("sess", "tm_1");
        Assert.Equal(TeamSubTaskStatus.Queued, task!.SubTasks[0].Status);
        Assert.Equal(2, task.SubTasks[0].Iteration);
        Assert.Equal(1, task.RepairRoundsUsed);
        Assert.Equal("unit tests added and green", task.SubTasks[0].AcceptanceCriteria);
    }

    [Fact]
    public async Task Redispatch_WithoutAllowRedispatch_StillAllowed()
    {
        // Profile no longer gates redispatch; only status + handoff + MaxRepairRounds matter.
        await SeedAsync(TeamSubTaskStatus.Failed);
        var tool = new TeamRedispatchTool(_settings, _tasks, _profiles, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_redispatch",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["subtask_id"] = "sub_a",
                    ["reason"] = "verification failed",
                    ["handoff"] = "fix the findings",
                    ["acceptance_criteria"] = "findings addressed"
                })),
            default);
        Assert.True(result.Succeeded, result.Error ?? result.Content ?? result.Summary);
    }

    [Fact]
    public async Task Redispatch_WhenMaxRepairRoundsReached_Denied()
    {
        await SeedAsync(TeamSubTaskStatus.Failed, repairRoundsUsed: 2);
        var tool = new TeamRedispatchTool(_settings, _tasks, _profiles, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_redispatch",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["subtask_id"] = "sub_a",
                    ["reason"] = "verification failed again",
                    ["handoff"] = "more findings",
                    ["acceptance_criteria"] = "still needs green build"
                })),
            default);
        Assert.False(result.Succeeded);
        var text = $"{result.Summary}\n{result.Error}\n{result.Content}";
        Assert.Contains("MaxRepairRounds", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateSubtask_UnknownDependsOn_Denied()
    {
        await SeedTeamForCreateAsync();
        var tool = new TeamCreateSubtaskTool(_settings, _tasks, _teams, _profiles, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_create_subtask",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["assignee_profile_id"] = "worker",
                    ["title"] = "qa check",
                    ["acceptance_criteria"] = "qa notes written",
                    ["handoff"] = "write qa notes",
                    ["depends_on"] = "sub-placeholder"
                })),
            default);
        Assert.False(result.Succeeded);
        var text = $"{result.Summary}\n{result.Error}\n{result.Content}";
        Assert.Contains("depends_on", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sub-placeholder", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateSubtask_MissingAcceptanceCriteria_Denied()
    {
        await SeedTeamForCreateAsync();
        var tool = new TeamCreateSubtaskTool(_settings, _tasks, _teams, _profiles, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_create_subtask",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["assignee_profile_id"] = "worker",
                    ["title"] = "qa check",
                    ["handoff"] = "do qa"
                })),
            default);
        Assert.False(result.Succeeded);
        var text = $"{result.Summary}\n{result.Error}\n{result.Content}";
        Assert.Contains("acceptance_criteria", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateSubtask_KnownDependsOn_Allowed()
    {
        await SeedTeamForCreateAsync(withExistingSub: true);
        var tool = new TeamCreateSubtaskTool(_settings, _tasks, _teams, _profiles, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_create_subtask",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["assignee_profile_id"] = "worker",
                    ["title"] = "qa check",
                    ["acceptance_criteria"] = "qa notes written",
                    ["handoff"] = "write qa notes",
                    ["facts"] = "repo builds",
                    ["constraints"] = "no new deps",
                    ["artifacts"] = "docs/qa.md",
                    ["depends_on"] = "sub_a"
                })),
            default);
        Assert.True(result.Succeeded, result.Error ?? result.Content ?? result.Summary);
        var task = await _tasks.GetAsync("sess", "tm_1");
        var created = Assert.Single(task!.SubTasks, s => s.Title == "qa check");
        Assert.Equal(TeamSubTaskStatus.Pending, created.Status);
        Assert.Equal("qa notes written", created.AcceptanceCriteria);
        Assert.Equal("repo builds", created.Facts);
        Assert.Equal("no new deps", created.Constraints);
        Assert.Contains("docs/qa.md", created.ArtifactRefs);
    }

    [Fact]
    public async Task Submit_Completed_WithoutVerifyPass_DeniedForCodeGoal()
    {
        await SeedAsync(TeamSubTaskStatus.Succeeded);
        var task = (await _tasks.GetAsync("sess", "tm_1"))!;
        task.Goal = "implement the fix and add unit tests";
        task.SubTasks[0].Result = """
            ## Result
            status: completed
            accomplished:
            - changed code
            """;
        await _tasks.SaveAsync(task);
        var tool = new TeamSubmitResultTool(_settings, _tasks, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_submit_result",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["result"] = "shipped",
                    ["disposition"] = "completed"
                })),
            default);
        Assert.False(result.Succeeded);
        var text = $"{result.Summary}\n{result.Error}\n{result.Content}";
        Assert.Contains("Verify required", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_Completed_WithVerifyPass_Allowed()
    {
        await SeedAsync(TeamSubTaskStatus.Succeeded);
        var task = (await _tasks.GetAsync("sess", "tm_1"))!;
        task.Goal = "implement the fix";
        task.SubTasks[0].Result = """
            ## Result
            status: completed
            verdict: pass
            findings:
            - green
            """;
        await _tasks.SaveAsync(task);
        var tool = new TeamSubmitResultTool(_settings, _tasks, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_submit_result",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["result"] = "shipped",
                    ["disposition"] = "completed"
                })),
            default);
        Assert.True(result.Succeeded, result.Error ?? result.Content ?? result.Summary);
    }

    [Fact]
    public async Task CancelSubtask_Queued_Allowed()
    {
        await SeedAsync(TeamSubTaskStatus.Queued);
        var tool = new TeamCancelSubtaskTool(_settings, _tasks, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_cancel_subtask",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["subtask_id"] = "sub_a",
                    ["reason"] = "obsolete after failure"
                })),
            default);
        Assert.True(result.Succeeded, result.Error ?? result.Content ?? result.Summary);
        var task = await _tasks.GetAsync("sess", "tm_1");
        Assert.Equal(TeamSubTaskStatus.Cancelled, task!.SubTasks[0].Status);
    }

    [Fact]
    public async Task Redispatch_MissingAcceptanceCriteria_Denied()
    {
        await SeedAsync(TeamSubTaskStatus.Failed);
        var tool = new TeamRedispatchTool(_settings, _tasks, _profiles, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_redispatch",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["subtask_id"] = "sub_a",
                    ["reason"] = "try again",
                    ["handoff"] = "more context"
                })),
            default);
        Assert.False(result.Succeeded);
        var text = $"{result.Summary}\n{result.Error}\n{result.Content}";
        Assert.Contains("acceptance_criteria", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_WithRunningSubtask_Denied()
    {
        await SeedAsync(TeamSubTaskStatus.Running);
        var tool = new TeamSubmitResultTool(_settings, _tasks, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_submit_result",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["result"] = "all done",
                    ["disposition"] = "completed"
                })),
            default);
        Assert.False(result.Succeeded);
        var text = $"{result.Summary}\n{result.Error}\n{result.Content}";
        Assert.Contains("Work remaining", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_WithFailedAndCompletedDisposition_Denied()
    {
        await SeedAsync(TeamSubTaskStatus.Failed);
        var tool = new TeamSubmitResultTool(_settings, _tasks, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_submit_result",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["result"] = "here is the release notes",
                    ["disposition"] = "completed"
                })),
            default);
        Assert.False(result.Succeeded);
        var text = $"{result.Summary}\n{result.Error}\n{result.Content}";
        Assert.Contains("Unresolved failures", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_WithFailedAndPartialDisposition_Allowed()
    {
        await SeedAsync(TeamSubTaskStatus.Failed);
        var tool = new TeamSubmitResultTool(_settings, _tasks, _traces);
        using var scope = TeamTaskExecutionScope.Enter((await _tasks.GetAsync("sess", "tm_1"))!);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_submit_result",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["result"] = "partial deliverable",
                    ["disposition"] = "partial"
                })),
            default);
        Assert.True(result.Succeeded, result.Error ?? result.Content ?? result.Summary);
        var task = await _tasks.GetAsync("sess", "tm_1");
        Assert.Equal(TeamTaskStatus.Succeeded, task!.Status);
        Assert.Equal("partial deliverable", task.FinalResult);
    }

    private async Task SeedAsync(TeamSubTaskStatus status, int repairRoundsUsed = 0)
    {
        await _profiles.SaveAsync(new AgentProfile
        {
            Id = "worker",
            Name = "Worker"
        });
        await _tasks.SaveAsync(new TeamTask
        {
            TaskId = "tm_1",
            TeamId = "t1",
            RootSessionId = "sess",
            Goal = "g",
            Status = TeamTaskStatus.Running,
            RepairRoundsUsed = repairRoundsUsed,
            SubTasks =
            [
                new TeamSubTask
                {
                    Id = "sub_a",
                    Title = "work",
                    AssigneeProfileId = "worker",
                    Status = status,
                    Iteration = 1,
                    Result = "prev"
                }
            ]
        });
    }

    private async Task SeedTeamForCreateAsync(bool withExistingSub = false)
    {
        await _profiles.SaveAsync(new AgentProfile { Id = "leader", Name = "Leader" });
        await _profiles.SaveAsync(new AgentProfile { Id = "worker", Name = "Worker" });
        await _teams.SaveAsync(new TeamDefinition
        {
            Id = "t1",
            Name = "Squad",
            LeaderProfileId = "leader",
            WorkerProfileIds = ["worker"]
        });
        await _tasks.SaveAsync(new TeamTask
        {
            TaskId = "tm_1",
            TeamId = "t1",
            RootSessionId = "sess",
            Goal = "g",
            Status = TeamTaskStatus.Running,
            SubTasks = withExistingSub
                ?
                [
                    new TeamSubTask
                    {
                        Id = "sub_a",
                        Title = "impl",
                        AssigneeProfileId = "worker",
                        Status = TeamSubTaskStatus.Succeeded,
                        Iteration = 1
                    }
                ]
                : []
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private sealed class TestPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, "skills");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(ConfigPath);
            Directory.CreateDirectory(SessionsPath);
            Directory.CreateDirectory(Path.Combine(ConfigPath, "teams"));
            Directory.CreateDirectory(Path.Combine(ConfigPath, "agent-profiles"));
        }

        public string ResolveSkillPath(string path) => path;
    }
}
