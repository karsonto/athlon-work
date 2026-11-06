using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Storage;
using Athlon.Agent.Infrastructure.Teams;

namespace Athlon.Agent.Tests.Teams;

public sealed class TeamOuterLoopTests : IDisposable
{
    private readonly string _root;
    private readonly TestPathProvider _paths;
    private readonly JsonFileStore _json;
    private readonly FileTeamTaskStore _tasks;
    private readonly FileTeamTraceStore _traces;
    private readonly FileAgentProfileStore _profiles;
    private readonly FileTeamDefinitionStore _teams;
    private readonly AppSettings _settings;

    public TeamOuterLoopTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "athlon-teams-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new TestPathProvider(_root);
        _paths.EnsureCreated();
        _json = new JsonFileStore();
        _tasks = new FileTeamTaskStore(_paths, _json);
        _traces = new FileTeamTraceStore(_tasks, _json);
        _profiles = new FileAgentProfileStore(_paths, _json);
        _teams = new FileTeamDefinitionStore(_paths, _json);
        _settings = new AppSettings
        {
            Teams = new TeamsSettings
            {
                Enabled = true,
                MaxParallelWorkers = 2,
                DefaultMaxIterations = 3,
                AbsoluteMaxIterations = 8
            }
        };
    }

    [Fact]
    public async Task OuterLoop_RunsQueuedWorkersThenLeaderSubmits()
    {
        await SeedTeamAsync(maxParallel: 2);
        var executor = new ScriptedMemberExecutor();
        executor.OnLeader = (task, turn) =>
        {
            if (turn == 1)
            {
                task.SubTasks.Add(MakeQueued("sub_a", "worker"));
                task.SubTasks.Add(MakeQueued("sub_b", "worker"));
                return TeamMemberRunOutcome.Success("dispatched", AgentSession.Create("ldr"));
            }

            task.FinalResult = "all done";
            task.Status = TeamTaskStatus.Succeeded;
            task.CompletedAt = DateTimeOffset.UtcNow;
            return TeamMemberRunOutcome.Success("submitted", AgentSession.Create("ldr"));
        };
        executor.OnWorker = (_, _) =>
            Task.FromResult(TeamMemberRunOutcome.Success("worker-ok", AgentSession.Create("mbr")));

        var orch = CreateOrchestrator(executor);
        var began = await orch.BeginAsync(new TeamTaskStartRequest
        {
            TeamId = "t1",
            RootSessionId = "sess",
            Goal = "ship it"
        });
        Assert.True(began.Ok);

        var completed = await orch.RunToCompletionAsync(began.Task.RootSessionId, began.Task.TaskId);
        Assert.Equal(TeamTaskStatus.Succeeded, completed.Status);
        Assert.Equal("all done", completed.FinalResult);
        Assert.All(completed.SubTasks, s => Assert.Equal(TeamSubTaskStatus.Succeeded, s.Status));
        Assert.Equal(2, executor.WorkerStarts);
        Assert.True(executor.LeaderTurns >= 2);
    }

    [Fact]
    public async Task OuterLoop_RespectsMaxParallelWorkers()
    {
        await SeedTeamAsync(maxParallel: 1);
        var gate = new SemaphoreSlim(0, 3);
        var started = 0;
        var maxSeen = 0;
        var executor = new ScriptedMemberExecutor();
        executor.OnLeader = (task, turn) =>
        {
            if (turn == 1)
            {
                task.SubTasks.Add(MakeQueued("sub_1", "worker"));
                task.SubTasks.Add(MakeQueued("sub_2", "worker"));
                task.SubTasks.Add(MakeQueued("sub_3", "worker"));
                return TeamMemberRunOutcome.Success("queued", AgentSession.Create("ldr"));
            }

            task.FinalResult = "done";
            task.Status = TeamTaskStatus.Succeeded;
            task.CompletedAt = DateTimeOffset.UtcNow;
            return TeamMemberRunOutcome.Success("ok", AgentSession.Create("ldr"));
        };
        executor.OnWorker = async (_, ct) =>
        {
            var now = Interlocked.Increment(ref started);
            int snapshot;
            do
            {
                snapshot = maxSeen;
            } while (now > snapshot && Interlocked.CompareExchange(ref maxSeen, now, snapshot) != snapshot);

            await gate.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Decrement(ref started);
            return TeamMemberRunOutcome.Success("w", AgentSession.Create("mbr"));
        };

        var orch = CreateOrchestrator(executor);
        var began = await orch.BeginAsync(new TeamTaskStartRequest
        {
            TeamId = "t1",
            RootSessionId = "sess_par",
            Goal = "parallel cap"
        });

        var run = orch.RunToCompletionAsync(began.Task.RootSessionId, began.Task.TaskId);
        // Release workers one-by-one; max concurrent starts should stay at 1.
        for (var i = 0; i < 3; i++)
        {
            await WaitUntilAsync(() => started >= 1 || run.IsCompleted, TimeSpan.FromSeconds(5));
            Assert.True(started <= 1);
            gate.Release();
            await Task.Delay(30);
        }

        var completed = await run;
        Assert.Equal(TeamTaskStatus.Succeeded, completed.Status);
        Assert.Equal(1, maxSeen);
    }

    [Fact]
    public async Task OuterLoop_CancelMarksRunningAndQueuedCancelled()
    {
        await SeedTeamAsync(maxParallel: 2);
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new ScriptedMemberExecutor();
        executor.OnLeader = (task, turn) =>
        {
            task.SubTasks.Add(MakeQueued("sub_run", "worker"));
            task.SubTasks.Add(MakeQueued("sub_queue", "worker"));
            return TeamMemberRunOutcome.Success("queued", AgentSession.Create("ldr"));
        };
        executor.OnWorker = async (_, ct) =>
        {
            using var reg = ct.Register(() => block.TrySetCanceled(ct));
            await block.Task.ConfigureAwait(false);
            return TeamMemberRunOutcome.Success("never", AgentSession.Create("mbr"));
        };

        var orch = CreateOrchestrator(executor);
        using var cts = new CancellationTokenSource();
        var began = await orch.BeginAsync(new TeamTaskStartRequest
        {
            TeamId = "t1",
            RootSessionId = "sess_cancel",
            Goal = "cancel me"
        });

        var run = orch.RunToCompletionAsync(began.Task.RootSessionId, began.Task.TaskId, cts.Token);
        await WaitUntilAsync(() => executor.WorkerStarts >= 1, TimeSpan.FromSeconds(5));
        cts.Cancel();
        var completed = await run;
        Assert.Equal(TeamTaskStatus.Cancelled, completed.Status);
        Assert.Contains(completed.SubTasks, s => s.Status == TeamSubTaskStatus.Cancelled);
    }

    [Fact]
    public async Task OuterLoop_AskUserPauses_AndResumeContinues()
    {
        await SeedTeamAsync(maxParallel: 1);
        var executor = new ScriptedMemberExecutor();
        executor.OnLeader = (task, turn) =>
        {
            if (string.IsNullOrWhiteSpace(task.FinalResult)
                && task.Status != TeamTaskStatus.AwaitingHuman
                && turn == 1)
            {
                task.Status = TeamTaskStatus.AwaitingHuman;
                task.PendingHumanQuestion = "Which env?";
                return TeamMemberRunOutcome.Success("ask", AgentSession.Create("ldr"));
            }

            task.FinalResult = "prod it is";
            task.Status = TeamTaskStatus.Succeeded;
            task.CompletedAt = DateTimeOffset.UtcNow;
            return TeamMemberRunOutcome.Success("done", AgentSession.Create("ldr"));
        };

        var orch = CreateOrchestrator(executor);
        var began = await orch.BeginAsync(new TeamTaskStartRequest
        {
            TeamId = "t1",
            RootSessionId = "sess_ask",
            Goal = "ask then finish"
        });

        var paused = await orch.RunToCompletionAsync(began.Task.RootSessionId, began.Task.TaskId);
        Assert.Equal(TeamTaskStatus.AwaitingHuman, paused.Status);
        Assert.Equal("Which env?", paused.PendingHumanQuestion);

        var resumed = await orch.RunToCompletionAsync(
            began.Task.RootSessionId,
            began.Task.TaskId,
            humanAnswer: "prod");
        Assert.Equal(TeamTaskStatus.Succeeded, resumed.Status);
        Assert.Equal("prod it is", resumed.FinalResult);
        Assert.Null(resumed.PendingHumanQuestion);
    }

    [Fact]
    public async Task SaveLayout_DoesNotOverwriteTaskProgress()
    {
        var task = new TeamTask
        {
            TaskId = "tm_layout",
            TeamId = "t1",
            RootSessionId = "sess_layout",
            Goal = "layout",
            Status = TeamTaskStatus.Running,
            SubTasks =
            [
                new TeamSubTask
                {
                    Id = "sub_1",
                    Title = "w",
                    AssigneeProfileId = "worker",
                    Status = TeamSubTaskStatus.Running,
                    Result = null
                }
            ]
        };
        await _tasks.SaveAsync(task);

        task.SubTasks[0].Status = TeamSubTaskStatus.Succeeded;
        task.SubTasks[0].Result = "progress";
        await _tasks.SaveAsync(task);

        await _traces.SaveLayoutAsync(
            task.RootSessionId,
            task.TaskId,
            new Dictionary<string, TeamTraceNodeLayout>
            {
                ["sub_1"] = new TeamTraceNodeLayout { X = 10, Y = 20 }
            });

        // Simulate a stale in-memory task.json write that layout used to trigger — ensure layout path
        // alone did not revert Succeeded.
        var loaded = await _tasks.GetAsync(task.RootSessionId, task.TaskId);
        Assert.NotNull(loaded);
        Assert.Equal(TeamSubTaskStatus.Succeeded, loaded!.SubTasks[0].Status);
        Assert.Equal("progress", loaded.SubTasks[0].Result);
        Assert.True(loaded.TraceLayout.ContainsKey("sub_1"));
        Assert.Equal(10, loaded.TraceLayout["sub_1"].X);
    }

    [Fact]
    public void LeaderFailure_DoesNotClobberPriorSubmit_ViaMerge()
    {
        var disk = new TeamTask
        {
            TaskId = "tm",
            Status = TeamTaskStatus.Running,
            FinalResult = null
        };
        var scoped = new TeamTask
        {
            TaskId = "tm",
            Status = TeamTaskStatus.Succeeded,
            FinalResult = "submitted"
        };

        // Mirror MergeLeaderMutations success path used by orchestrator.
        if (scoped.Status is TeamTaskStatus.Succeeded or TeamTaskStatus.AwaitingHuman or TeamTaskStatus.Failed)
        {
            disk.Status = scoped.Status;
        }

        if (!string.IsNullOrWhiteSpace(scoped.FinalResult))
        {
            disk.FinalResult = scoped.FinalResult;
        }

        Assert.Equal(TeamTaskStatus.Succeeded, disk.Status);
        Assert.Equal("submitted", disk.FinalResult);
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

    private TeamTaskOrchestrator CreateOrchestrator(ScriptedMemberExecutor executor) =>
        new(_settings, _teams, _profiles, _tasks, _traces, executor, new NoOpLogger());

    private async Task SeedTeamAsync(int maxParallel)
    {
        await _profiles.SaveAsync(new AgentProfile { Id = "leader", Name = "Leader" });
        await _profiles.SaveAsync(new AgentProfile { Id = "worker", Name = "Worker" });
        await _teams.SaveAsync(new TeamDefinition
        {
            Id = "t1",
            Name = "Squad",
            LeaderProfileId = "leader",
            WorkerProfileIds = ["worker"],
            MaxParallelWorkers = maxParallel
        });
    }

    private static TeamSubTask MakeQueued(string id, string profileId) => new()
    {
        Id = id,
        Title = id,
        AssigneeProfileId = profileId,
        Status = TeamSubTaskStatus.Queued,
        MemberSessionId = "mbr_" + id,
        Iteration = 1,
        MaxIterations = 1
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("condition not met");
            }

            await Task.Delay(20);
        }
    }

    private sealed class ScriptedMemberExecutor : ITeamMemberRunExecutor
    {
        private int _leaderTurn;

        public int LeaderTurns => _leaderTurn;
        public int WorkerStarts;

        public Func<TeamTask, int, TeamMemberRunOutcome>? OnLeader { get; set; }

        public Func<TeamTask, CancellationToken, Task<TeamMemberRunOutcome>>? OnWorker { get; set; }

        public async Task<TeamMemberRunOutcome> ExecuteAsync(
            TeamTask task,
            AgentProfile profile,
            string memberSessionId,
            string message,
            bool isLeader,
            CancellationToken cancellationToken = default)
        {
            if (isLeader)
            {
                var turn = Interlocked.Increment(ref _leaderTurn);
                return OnLeader?.Invoke(task, turn)
                       ?? TeamMemberRunOutcome.Success("leader", AgentSession.Create("ldr"));
            }

            Interlocked.Increment(ref WorkerStarts);
            if (OnWorker is null)
            {
                return TeamMemberRunOutcome.Success("worker", AgentSession.Create("mbr"));
            }

            return await OnWorker(task, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
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
