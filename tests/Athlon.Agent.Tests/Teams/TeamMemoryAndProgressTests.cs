using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Storage;
using Athlon.Agent.Infrastructure.Teams;

namespace Athlon.Agent.Tests.Teams;

public sealed class TeamMemoryAndProgressTests : IDisposable
{
    private readonly string _root;
    private readonly TestPathProvider _paths;
    private readonly FileTeamTaskStore _tasks;
    private readonly FileTeamMemoryStore _memory;
    private readonly AppSettings _settings = new() { Teams = new TeamsSettings { Enabled = true } };

    public TeamMemoryAndProgressTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "athlon-team-mem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new TestPathProvider(_root);
        _paths.EnsureCreated();
        _tasks = new FileTeamTaskStore(_paths, new JsonFileStore());
        _memory = new FileTeamMemoryStore(_tasks);
    }

    [Fact]
    public async Task Memory_PutGetList_RoundTrip()
    {
        await SeedTaskAsync();
        await _memory.PutAsync("sess", "tm_1", new TeamMemoryEntry
        {
            Key = "note:1",
            Content = "artifact at docs/out.md",
            Tags = ["docs"],
            AuthorProfileId = "worker-a"
        });

        var got = await _memory.GetAsync("sess", "tm_1", "note:1");
        Assert.NotNull(got);
        Assert.Contains("docs/out.md", got!.Content);

        var listed = await _memory.ListAsync("sess", "tm_1");
        Assert.Contains(listed, e => e.Key == "note:1");
    }

    [Fact]
    public async Task TeamProgress_FromProfileScope_ShowsSiblingStatus()
    {
        await SeedTaskAsync();
        var tool = new TeamProgressTool(_settings, _tasks);
        using var scope = AgentProfileTurnScope.Enter(new AgentProfileTurnOptions(
            ProfileId: "worker-b",
            TeamTaskId: "tm_1",
            RootSessionId: "sess",
            IsLeader: false,
            ToolAllowList: TeamToolDefaults.DefaultWorkerAllowList));

        var result = await tool.InvokeAsync(
            new ToolInvocation("team_progress", ToolCallArguments.Empty),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("sub_a", result.Content);
        Assert.Contains("Succeeded", result.Content);
        Assert.Contains("sub_b", result.Content);
    }

    [Fact]
    public async Task TeamMemoryTool_WorkerCanReadSiblingWrite()
    {
        await SeedTaskAsync();
        await _memory.PutAsync("sess", "tm_1", new TeamMemoryEntry
        {
            Key = "subtask:sub_a:result",
            Content = "## Result\nstatus: completed\n",
            AuthorProfileId = "worker-a"
        });

        var tool = new TeamMemoryTool(_settings, _tasks, _memory);
        using var scope = AgentProfileTurnScope.Enter(new AgentProfileTurnOptions(
            ProfileId: "worker-b",
            TeamTaskId: "tm_1",
            RootSessionId: "sess",
            IsLeader: false));

        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "team_memory",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["action"] = "get",
                    ["key"] = "subtask:sub_a:result"
                })),
            default);
        Assert.True(result.Succeeded);
        Assert.Contains("completed", result.Content);
    }

    [Fact]
    public void RefreshReady_CancelsDownstreamWhenDependencyFailed()
    {
        var task = new TeamTask
        {
            SubTasks =
            [
                new TeamSubTask { Id = "a", Status = TeamSubTaskStatus.Failed },
                new TeamSubTask { Id = "b", Status = TeamSubTaskStatus.Pending, DependsOn = ["a"] }
            ]
        };
        TeamWorkerHelpers.RefreshReady(task);
        Assert.Equal(TeamSubTaskStatus.Cancelled, task.SubTasks[1].Status);
    }

    private async Task SeedTaskAsync()
    {
        await _tasks.SaveAsync(new TeamTask
        {
            TaskId = "tm_1",
            TeamId = "t1",
            RootSessionId = "sess",
            Goal = "ship",
            Status = TeamTaskStatus.Running,
            SubTasks =
            [
                new TeamSubTask
                {
                    Id = "sub_a",
                    Title = "impl",
                    Status = TeamSubTaskStatus.Succeeded,
                    AssigneeProfileId = "worker-a",
                    Result = "done"
                },
                new TeamSubTask
                {
                    Id = "sub_b",
                    Title = "verify",
                    Status = TeamSubTaskStatus.Ready,
                    AssigneeProfileId = "worker-b",
                    DependsOn = ["sub_a"]
                }
            ]
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
