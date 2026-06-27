using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.SubAgents;
using Athlon.Agent.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Tests;

public sealed class SubAgentSessionsTests
{
    [Fact]
    public async Task SpawnAsync_CreatesSession_WithSessionKey()
    {
        await using var env = await SubAgentTestEnvironment.CreateAsync();
        var result = await env.Manager.SpawnAsync(
            env.ParentSessionId,
            "Researcher",
            "find auth code",
            null,
            30);

        Assert.Equal("ok", result.Status);
        Assert.StartsWith("sub:", result.SessionKey, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.SubSessionId));
        Assert.Equal(1, env.Orchestrator.SendCount);
    }

    [Fact]
    public async Task SpawnAsync_ReusesLabel()
    {
        await using var env = await SubAgentTestEnvironment.CreateAsync();
        var first = await env.Manager.SpawnAsync(env.ParentSessionId, "Researcher", "first", "auth-refactor", 30);
        var second = await env.Manager.SpawnAsync(env.ParentSessionId, "Researcher", "second", "auth-refactor", 30);

        Assert.Equal(first.SubSessionId, second.SubSessionId);
        Assert.True(second.ReusedExisting);
        Assert.Equal(2, env.Orchestrator.SendCount);
    }

    [Fact]
    public async Task SendAsync_ReturnsStructuredReply()
    {
        await using var env = await SubAgentTestEnvironment.CreateAsync();
        var spawn = await env.Manager.SpawnAsync(env.ParentSessionId, "Worker", "hello", null, 30);
        var send = await env.Manager.SendAsync(env.ParentSessionId, spawn.SessionKey, null, "again", 30);

        Assert.Equal("ok", send.Status);
        Assert.Contains("<<<BEGIN_UNTRUSTED_CHILD_RESULT>>>", send.Reply ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpawnAsync_TimeoutZero_ReturnsTaskId()
    {
        await using var env = await SubAgentTestEnvironment.CreateAsync();
        env.BackgroundExecutor.Start();

        var result = await env.Manager.SpawnAsync(env.ParentSessionId, "Worker", "async work", null, 0);

        Assert.Equal("accepted", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.TaskId));
    }

    [Fact]
    public async Task ListAndHistory_WorkAfterSpawn()
    {
        await using var env = await SubAgentTestEnvironment.CreateAsync();
        var spawn = await env.Manager.SpawnAsync(env.ParentSessionId, "Worker", "list me", null, 30);

        var list = await env.Manager.ListAsync(env.ParentSessionId);
        Assert.Contains(list, entry => entry.SessionKey == spawn.SessionKey);

        var history = await env.Manager.HistoryAsync(env.ParentSessionId, spawn.SessionKey, 10);
        Assert.Null(history.Error);
        Assert.Contains("[Assistant]", history.Content ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingCompletions_DrainAfterSyncSend()
    {
        await using var env = await SubAgentTestEnvironment.CreateAsync();
        var spawn = await env.Manager.SpawnAsync(env.ParentSessionId, "Worker", "complete", null, 30);
        var drained = await env.Manager.DrainCompletionsAsync(env.ParentSessionId, 5);

        Assert.NotEmpty(drained);
        Assert.Contains(spawn.SessionKey, drained[0].AnnounceText, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildRouter_ExcludesSessionsTools()
    {
        var settings = new AppSettings();
        var tools = new IAgentTool[]
        {
            new StubSessionsTool("sessions_spawn"),
            new StubNamedTool("file_list")
        };
        var router = new ChildAgentToolRouter(
            tools,
            new EmptyMcpRegistry(),
            settings,
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new AgentRunContextAccessor(),
            RouterTestDependencies.CreateWorkspaceGuard());

        var names = router.ListTools().Select(tool => tool.Name).ToArray();
        Assert.DoesNotContain("sessions_spawn", names);
        Assert.Contains("file_list", names);
    }

    private sealed class StubSessionsTool(string name) : IAgentTool, ISubAgentTool, IExcludedFromChildAgentToolkit
    {
        public ToolDefinition Definition => new(name, name, new Dictionary<string, string>());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubNamedTool(string name) : IAgentTool
    {
        public ToolDefinition Definition => new(name, name, new Dictionary<string, string>());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class EmptyMcpRegistry : IMcpRegistry
    {
        public IReadOnlyList<McpServerStatus> GetStatuses() => Array.Empty<McpServerStatus>();
        public IReadOnlyList<ToolDefinition> ListToolDefinitions() => Array.Empty<ToolDefinition>();
        public IReadOnlyList<McpCatalogEntry> ListCatalogEntries() => Array.Empty<McpCatalogEntry>();
        public int CatalogVersion => 0;
        public int CatalogCount => 0;
        public int CatalogSchemaCharCount => 0;
        public IReadOnlyList<McpSearchIndex.SearchResult> SearchCatalog(string query, int topK, double minScore, string? serverName = null) =>
            Array.Empty<McpSearchIndex.SearchResult>();
        public Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<ToolResult> InvokeAsync(string serverName, string toolName, IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Failure("none", "none"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SubAgentTestEnvironment : IAsyncDisposable
    {
        public string ParentSessionId { get; } = "parent-sessions-test";
        public StubOrchestrator Orchestrator { get; }
        public SubAgentSessionManager Manager { get; }
        public SubAgentBackgroundExecutor BackgroundExecutor { get; }
        private readonly TempDirectoryScope _temp;

        private SubAgentTestEnvironment(TempDirectoryScope temp, StubOrchestrator orchestrator, SubAgentSessionManager manager, SubAgentBackgroundExecutor backgroundExecutor)
        {
            _temp = temp;
            Orchestrator = orchestrator;
            Manager = manager;
            BackgroundExecutor = backgroundExecutor;
        }

        public static async Task<SubAgentTestEnvironment> CreateAsync()
        {
            var temp = new TempDirectoryScope("athlon-subagent-sessions");
            var paths = new SessionsTestAppPathProvider(temp.Root);
            paths.EnsureCreated();
            var json = new JsonFileStore();
            var settings = new AppSettings();
            var registry = new FileSubAgentRegistry(paths, json);
            var sessionStore = new FileSubAgentSessionStore(paths, json);
            var taskStore = new FileSubAgentTaskStore(paths, json);
            var completionStore = new FileSubAgentCompletionStore(paths, json);
            var orchestrator = new StubOrchestrator();
            var storage = new ParentSessionStorage(paths.SessionsPath, ParentSessionId: "parent-sessions-test");
            var childRouter = new Lazy<ChildAgentToolRouter>(() =>
                new ChildAgentToolRouter(
                    Array.Empty<IAgentTool>(),
                    new EmptyMcpRegistry(),
                    settings,
                    new ActiveAgentSessionContext(),
                    RouterTestDependencies.CreateSessionKnowledgeState(),
                    RouterTestDependencies.CreateSessionHarnessState(),
                    new AgentRunContextAccessor(),
                    RouterTestDependencies.CreateWorkspaceGuard()));
            var prompt = new SubAgentSystemPromptOrchestrator(
                settings,
                new StubHostEnvironment(paths.SkillsPath),
                NullCurrentSsoUserContext.Instance,
                Array.Empty<Athlon.Agent.Core.Prompt.IEnvironmentPromptSection>(),
                Array.Empty<Athlon.Agent.Core.Prompt.IPreReasoningPromptContributor>());
            var services = new ServiceCollection();
            services.AddSingleton<IAgentOrchestrator>(orchestrator);
            var serviceProvider = services.BuildServiceProvider();
            var runExecutor = new SubAgentRunExecutor(
                settings,
                serviceProvider,
                storage,
                sessionStore,
                childRouter,
                prompt,
                new ActiveAgentSessionContext(),
                new AgentRunContextAccessor(),
                new SessionsNoOpAppLogger());
            var backgroundExecutor = new SubAgentBackgroundExecutor(
                settings,
                runExecutor,
                registry,
                taskStore,
                completionStore,
                new ServiceCollection().BuildServiceProvider(),
                new SessionsNoOpAppLogger());
            var manager = new SubAgentSessionManager(
                settings,
                registry,
                sessionStore,
                taskStore,
                completionStore,
                runExecutor,
                backgroundExecutor,
                new SessionsNoOpAppLogger());

            await storage.SaveSessionAsync(
                AgentSession.Create("Parent") with { Id = "parent-sessions-test", ActiveWorkspace = temp.Root });

            return new SubAgentTestEnvironment(temp, orchestrator, manager, backgroundExecutor);
        }

        public ValueTask DisposeAsync()
        {
            BackgroundExecutor.Stop();
            _temp.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SessionsTestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(root, "config");
        public string SessionsPath => Path.Combine(root, "sessions");
        public string AuditPath => Path.Combine(root, "audit");
        public string LogsPath => Path.Combine(root, "logs");
        public string CredentialsPath => Path.Combine(root, "credentials");
        public string SkillsPath => Path.Combine(root, "skills");
        public void EnsureCreated() => Directory.CreateDirectory(root);
        public string ResolveSkillPath(string path) => Path.Combine(SkillsPath, path);
    }

    private sealed class ParentSessionStorage(string sessionsPath, string ParentSessionId) : IFileStorageService
    {
        public string RootPath => sessionsPath;
        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(sessionId, ParentSessionId, StringComparison.Ordinal))
            {
                return Task.FromResult<AgentSession?>(null);
            }

            return Task.FromResult<AgentSession?>(
                AgentSession.Create("Parent") with { Id = ParentSessionId, ActiveWorkspace = sessionsPath });
        }

        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
    }

    private sealed class StubOrchestrator : IAgentOrchestrator
    {
        public int SendCount { get; private set; }

        public Task<AgentSession> SendAsync(
            AgentSession session,
            string userInput,
            IReadOnlyList<ImageAttachment>? imageAttachments = null,
            AgentTurnCallbacks? callbacks = null,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            var assistant = ChatMessage.Create(MessageRole.Assistant, "done from sub", session.Messages.LastOrDefault()?.Id);
            return Task.FromResult(session.WithMessage(assistant));
        }
    }

    private sealed class StubHostEnvironment(string skillsPath) : IAgentHostEnvironment
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
        public string SkillsDirectory => skillsPath;
    }

    private sealed class SessionsNoOpAppLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
