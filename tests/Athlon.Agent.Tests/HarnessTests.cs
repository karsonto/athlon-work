using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Middleware;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Harness;
using Athlon.Agent.Infrastructure.Memory;

namespace Athlon.Agent.Tests;

public sealed class HarnessTests
{
    [Fact]
    public async Task SessionHarnessState_PersistsEnabledFlag()
    {
        var root = CreateTempRoot();
        var state = CreateHarnessState(root);

        await state.SaveAsync("session-1", new SessionHarnessSnapshot(true));

        var reloaded = CreateHarnessState(root);
        await reloaded.LoadAsync("session-1");

        Assert.True(reloaded.IsEnabled("session-1"));
    }

    [Fact]
    public async Task TodoWriteTool_ReplaceAndMergeTasks()
    {
        var root = CreateTempRoot();
        var store = CreateTaskStore(root);
        var sessionContext = new ActiveAgentSessionContext();
        sessionContext.SetSession("session-1");
        var notifier = new TaskListChangedNotifier();
        var tool = new TodoWriteTool(store, sessionContext, notifier, new HarnessNoOpAppLogger());

        var replace = await tool.InvokeAsync(new ToolInvocation("todo_write", new Dictionary<string, string>
        {
            ["todos"] = """[{"id":"1","content":"first","status":"pending"},{"id":"2","content":"second","status":"in_progress"}]""",
            ["merge"] = "false"
        }));
        Assert.True(replace.Succeeded);

        var merge = await tool.InvokeAsync(new ToolInvocation("todo_write", new Dictionary<string, string>
        {
            ["todos"] = """[{"id":"1","content":"first","status":"completed"}]""",
            ["merge"] = "true"
        }));
        Assert.True(merge.Succeeded);

        var list = await store.GetAsync("session-1");
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("completed", list.Items.First(item => item.Id == "1").Status, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("in_progress", list.Items.First(item => item.Id == "2").Status, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaskListPromptContributor_InjectsTasks_WhenHarnessEnabled()
    {
        var harness = RouterTestDependencies.CreateSessionHarnessState(enabled: true);
        var store = new InMemoryTaskListStore(
        [
            new AgentTaskItem { Id = "1", Content = "Do work", Status = AgentTaskStatuses.Pending }
        ]);
        var accessor = RouterTestDependencies.CreateRunContextAccessor(harnessEnabled: true);
        var contributor = new TaskListPromptContributor(harness, store, accessor);
        var builder = new StringBuilder();
        var context = CreatePromptContext();

        contributor.Append(builder, context);

        Assert.Contains("## Current Task List", builder.ToString(), StringComparison.Ordinal);
        Assert.Contains("Do work", builder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryPromptContributor_Skips_WhenHarnessDisabled()
    {
        var memory = new StubLongTermMemory("remember this");
        var harness = RouterTestDependencies.CreateSessionHarnessState(enabled: false);
        var contributor = new MemoryPromptContributor(memory, harness, new AgentRunContextAccessor());
        var builder = new StringBuilder();

        contributor.Append(builder, CreatePromptContext());

        Assert.DoesNotContain("Long-Term Memory", builder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTurnMemoryMiddleware_Skips_WhenHarnessDisabled()
    {
        var processor = new RecordingPostTurnMemoryProcessor();
        var middleware = new PostTurnMemoryMiddleware(
            RouterTestDependencies.CreateSessionHarnessState(enabled: false),
            new AgentRunContextAccessor(),
            processor,
            new HarnessNoOpAppLogger());
        var invocation = CreateTurnInvocation(RouterTestDependencies.CreateRunContextAccessor(harnessEnabled: false));

        await middleware.OnTurnCompletedAsync(invocation, CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(0, processor.CallCount);
    }

    [Fact]
    public async Task PostTurnMemoryMiddleware_Flushes_WhenHarnessEnabledOnRootRun()
    {
        var processor = new RecordingPostTurnMemoryProcessor();
        var accessor = RouterTestDependencies.CreateRunContextAccessor(harnessEnabled: true);
        var middleware = new PostTurnMemoryMiddleware(
            RouterTestDependencies.CreateSessionHarnessState(enabled: true),
            accessor,
            processor,
            new HarnessNoOpAppLogger());
        var invocation = CreateTurnInvocation(accessor);

        await middleware.OnTurnCompletedAsync(invocation, CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(1, processor.CallCount);
    }

    private static AgentTurnInvocation CreateTurnInvocation(AgentRunContextAccessor accessor)
    {
        var session = AgentSession.Create("test") with { Id = "test-session" };
        return new AgentTurnInvocation
        {
            RunContext = accessor.Current
                ?? AgentRunContext.CreateRoot(
                    session,
                    "run-1",
                    new ToolRouter(Array.Empty<IAgentTool>()),
                    PromptTestHelpers.CreateStaticOrchestrator(),
                    []),
            Session = session,
            StreamAdapter = new AgentStreamAdapter(session.Id, "run-1")
        };
    }

    private static EnvironmentPromptContext CreatePromptContext() =>
        new()
        {
            Session = AgentSession.Create("test"),
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "workspace"),
            Tools = Array.Empty<ToolDefinition>(),
            SkillsDirectory = Path.GetTempPath(),
            Host = new StubHostEnvironment(),
            PromptSettings = new PromptSettings()
        };

    private static SessionHarnessState CreateHarnessState(string root)
    {
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        return new SessionHarnessState(paths, new JsonFileStore(), new AgentRunContextAccessor());
    }

    private static FileSessionTaskListStore CreateTaskStore(string root)
    {
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        return new FileSessionTaskListStore(paths, new JsonFileStore(), new AgentRunContextAccessor());
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "athlon-harness-" + Guid.NewGuid().ToString("N"));

    private sealed class InMemoryTaskListStore(IReadOnlyList<AgentTaskItem> seed) : ISessionTaskListStore
    {
        private SessionTaskList _list = new() { Items = seed.ToList() };

        public Task<SessionTaskList> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_list);

        public Task ReplaceAsync(string sessionId, SessionTaskList list, CancellationToken cancellationToken = default)
        {
            _list = list;
            return Task.CompletedTask;
        }

        public Task<SessionTaskList> ApplyMergeAsync(
            string sessionId,
            IReadOnlyList<AgentTaskItem> todos,
            bool merge,
            CancellationToken cancellationToken = default)
        {
            if (merge)
            {
                var byId = _list.Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var todo in todos)
                {
                    byId[todo.Id] = todo;
                }

                _list = new SessionTaskList { Items = byId.Values.ToList() };
            }
            else
            {
                _list = new SessionTaskList { Items = todos.ToList() };
            }

            return Task.FromResult(_list);
        }
    }

    private sealed class StubLongTermMemory(string curated) : ILongTermMemory
    {
        public Task<string> ReadCuratedAsync(CancellationToken cancellationToken = default) => Task.FromResult(curated);
        public Task<string> ReadDailyAsync(DateTime date, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<string> ReadDailyFileAsync(string fileName, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task AppendDailyAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteCuratedAsync(string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DateTime> ReadWatermarkAsync(CancellationToken cancellationToken = default) => Task.FromResult(DateTime.MinValue);
        public Task WriteWatermarkAsync(DateTime watermark, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListDailyFilesAfterAsync(DateTime after, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<string>> ListAllMemoryFilePathsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["MEMORY.md"]);
        public Task ArchiveDailyFileAsync(string relativePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class HarnessNoOpAppLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }

    private sealed class RecordingPostTurnMemoryProcessor : IPostTurnMemoryProcessor
    {
        public int CallCount { get; private set; }

        public Task ProcessAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHostEnvironment : IAgentHostEnvironment
    {
        public bool IsWindows => true;
        public string OsDescription => "test";
        public string OsVersion => "1";
        public string UserName => "u";
        public string UserDomainName => "d";
        public string MachineName => "m";
        public string UserProfilePath => "/u";
        public string SystemDirectory => "/s";
        public string ProcessArchitecture => "x64";
        public string OsArchitecture => "x64";
        public int ProcessorCount => 1;
        public string AppDataDirectory => "/a";
        public string SkillsDirectory => "/skills";
    }

    private sealed class TestPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, AppPathProvider.SkillsFolderName);

        public void EnsureCreated()
        {
            Directory.CreateDirectory(SessionsPath);
        }

        public string ResolveSkillPath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
