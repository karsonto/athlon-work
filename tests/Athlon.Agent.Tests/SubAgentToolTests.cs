using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.SubAgents;
using Athlon.Agent.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Tests;

public sealed class SubAgentToolTests
{
    [Fact]
    public async Task InvokeAsync_NewSession_RequiresRole()
    {
        var tool = CreateTool(new StubOrchestrator());
        using var parent = new NoOpActiveAgentSessionContext().Enter("parent-1");

        var result = await tool.InvokeAsync(new ToolInvocation(
            "call_assistant",
            new Dictionary<string, string> { ["message"] = "do work" }));

        Assert.False(result.Succeeded);
        Assert.Contains("role", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_NewSession_ReturnsSessionId()
    {
        var orchestrator = new StubOrchestrator();
        var tool = CreateTool(orchestrator);
        using var parent = new NoOpActiveAgentSessionContext().Enter("parent-1");

        var result = await tool.InvokeAsync(new ToolInvocation(
            "call_assistant",
            new Dictionary<string, string>
            {
                ["role"] = "Searcher",
                ["message"] = "find todos"
            }));

        Assert.True(result.Succeeded);
        Assert.Contains("session_id:", result.Content ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, orchestrator.SendCount);
    }

    [Fact]
    public async Task InvokeAsync_Continue_ReusesSavedRole()
    {
        var store = new InMemorySubAgentStore();
        var subId = "sub-continue";
        var session = AgentSession.Create("Sub") with { Id = subId };
        await store.SaveAsync("parent-1", subId, new SubAgentSessionBundle(session, "Original role"));

        var orchestrator = new StubOrchestrator();
        var tool = CreateTool(orchestrator, store);
        using var parent = new NoOpActiveAgentSessionContext().Enter("parent-1");

        var result = await tool.InvokeAsync(new ToolInvocation(
            "call_assistant",
            new Dictionary<string, string>
            {
                ["session_id"] = subId,
                ["message"] = "continue"
            }));

        Assert.True(result.Succeeded);
        var loaded = await store.LoadAsync("parent-1", subId);
        Assert.Equal("Original role", loaded!.Role);
    }

    [Fact]
    public async Task InvokeAsync_Continue_CanOverrideRole()
    {
        var store = new InMemorySubAgentStore();
        var subId = "sub-override";
        var session = AgentSession.Create("Sub") with { Id = subId };
        await store.SaveAsync("parent-1", subId, new SubAgentSessionBundle(session, "Old"));

        var tool = CreateTool(new StubOrchestrator(), store);
        using var parent = new NoOpActiveAgentSessionContext().Enter("parent-1");

        await tool.InvokeAsync(new ToolInvocation(
            "call_assistant",
            new Dictionary<string, string>
            {
                ["session_id"] = subId,
                ["role"] = "New role",
                ["message"] = "go"
            }));

        var loaded = await store.LoadAsync("parent-1", subId);
        Assert.Equal("New role", loaded!.Role);
    }

    [Fact]
    public async Task InvokeAsync_ExceedsNestingDepth_Fails()
    {
        var settings = new AppSettings { SubAgent = new SubAgentSettings { MaxNestingDepth = 1 } };
        var tool = CreateTool(new StubOrchestrator(), settings: settings);
        using var parent = new NoOpActiveAgentSessionContext().Enter("parent-1");
        using var depth = SubAgentExecutionScope.Enter();

        var result = await tool.InvokeAsync(new ToolInvocation(
            "call_assistant",
            new Dictionary<string, string>
            {
                ["role"] = "x",
                ["message"] = "y"
            }));

        Assert.False(result.Succeeded);
        Assert.Contains("depth", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static SubAgentTool CreateTool(
        IAgentOrchestrator orchestrator,
        ISubAgentSessionStore? store = null,
        AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        store ??= new InMemorySubAgentStore();
        var storage = new ParentSessionStorage();
        var childRouter = new Lazy<ChildAgentToolRouter>(() =>
            new ChildAgentToolRouter(
                Array.Empty<IAgentTool>(),
                new EmptyMcpRegistry(),
                settings,
                RouterTestDependencies.CreateSessionContext(),
                RouterTestDependencies.CreateSessionKnowledgeState(),
                RouterTestDependencies.CreateWorkspaceGuard()));
        var prompt = new SubAgentSystemPromptOrchestrator(
            settings,
            new StubHostEnvironment(),
            Array.Empty<Athlon.Agent.Core.Prompt.IEnvironmentPromptSection>(),
            Array.Empty<Athlon.Agent.Core.Prompt.IPreReasoningPromptContributor>());

        var services = new ServiceCollection();
        services.AddSingleton(orchestrator);
        var serviceProvider = services.BuildServiceProvider();

        return new SubAgentTool(
            settings,
            serviceProvider,
            storage,
            store,
            childRouter,
            prompt,
            new NoOpActiveAgentSessionContext(),
            new AgentRunContextAccessor(),
            new NoOpAppLogger());
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

    private sealed class InMemorySubAgentStore : ISubAgentSessionStore
    {
        private readonly Dictionary<string, SubAgentSessionBundle> _bundles = new(StringComparer.Ordinal);

        public Task<SubAgentSessionBundle?> LoadAsync(string parentSessionId, string subSessionId, CancellationToken cancellationToken = default)
        {
            _bundles.TryGetValue(Key(parentSessionId, subSessionId), out var bundle);
            return Task.FromResult(bundle);
        }

        public Task SaveAsync(string parentSessionId, string subSessionId, SubAgentSessionBundle bundle, CancellationToken cancellationToken = default)
        {
            _bundles[Key(parentSessionId, subSessionId)] = bundle;
            return Task.CompletedTask;
        }

        private static string Key(string parent, string sub) => parent + ":" + sub;
    }

    private sealed class ParentSessionStorage : IFileStorageService
    {
        public string RootPath => "/tmp";
        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentSession?>(AgentSession.Create("Parent") with { Id = sessionId, ActiveWorkspace = @"C:\repo" });
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
        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
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

    private sealed class NoOpAppLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
