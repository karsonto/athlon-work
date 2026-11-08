using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Storage;
using Athlon.Agent.Infrastructure.Teams;

namespace Athlon.Agent.Tests.Teams;

public sealed class FileTeamStoresTests : IDisposable
{
    private readonly string _root;
    private readonly TestPathProvider _paths;
    private readonly JsonFileStore _json;

    public FileTeamStoresTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "athlon-teams-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new TestPathProvider(_root);
        _json = new JsonFileStore();
        _paths.EnsureCreated();
    }

    [Fact]
    public async Task ProfileAndTeam_RoundTrip()
    {
        var profiles = new FileAgentProfileStore(_paths, _json);
        var teams = new FileTeamDefinitionStore(_paths, _json);

#pragma warning disable CS0618 // Obsolete profile fields must still round-trip for older JSON
        var profile = new AgentProfile
        {
            Id = "p1",
            Name = "Worker",
            AllowRedispatch = true,
            MaxIterations = 3,
            SkillNames = ["a"]
        };
        await profiles.SaveAsync(profile);
        var loaded = await profiles.GetAsync("p1");
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.MaxIterations);
#pragma warning restore CS0618

        var team = new TeamDefinition
        {
            Id = "t1",
            Name = "Squad",
            LeaderProfileId = "p1",
            WorkerProfileIds = ["p1"]
        };
        await teams.SaveAsync(team);
        var listed = await teams.ListAsync();
        Assert.Contains(listed, t => t.Id == "t1");
    }

    [Fact]
    public async Task TaskAndTrace_RoundTrip()
    {
        var tasks = new FileTeamTaskStore(_paths, _json);
        var traces = new FileTeamTraceStore(tasks, _json);
        var task = new TeamTask
        {
            TaskId = "tm_1",
            TeamId = "t1",
            RootSessionId = "sess_1",
            Goal = "do it",
            Status = TeamTaskStatus.Running
        };
        await tasks.SaveAsync(task);
        await traces.AppendAsync("sess_1", "tm_1", new TeamTraceEvent { Type = "task.started", Message = "hi" });
        var events = await traces.ReadAsync("sess_1", "tm_1");
        Assert.Single(events);
        Assert.Equal("task.started", events[0].Type);

        var memberDir = tasks.ResolveMemberDirectory("sess_1", "tm_1", "mbr_1");
        Assert.Contains(Path.Combine("teams", "tm_1", "members", "mbr_1"), memberDir);
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
            // ignore cleanup failures on Windows file locks
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
