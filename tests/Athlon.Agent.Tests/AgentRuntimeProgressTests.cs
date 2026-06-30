using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

public sealed class AgentRuntimeProgressTests
{
    [Fact]
    public async Task SendAsync_EmitsToolStartedAndMessagesInOrder()
    {
        var storage = new NoOpStorage();
        var modelClient = new ScriptedModelClient(
            new AgentModelResponse(string.Empty, new[]
            {
                new AgentToolCall("call-1", "alpha", new Dictionary<string, string>()),
                new AgentToolCall("call-2", "beta", new Dictionary<string, string>())
            }),
            new AgentModelResponse("done", Array.Empty<AgentToolCall>()));

        var toolRouter = new ScriptedToolRouter();
        var settings = new AppSettings();
        var logger = new NoOpLogger();
        var (pipeline, compaction) = AgentRuntimeTestFactory.CreateMiddleware(
            new NoOpPreCompletionPipeline(), storage, new TokenEstimatorCalibrator(settings), settings, logger);
        var runtime = new AgentRuntime(
            modelClient,
            storage,
            toolRouter,
            PromptTestHelpers.CreateStaticOrchestrator("test prompt"),
            new NoOpPreCompletionPipeline(),
            new PassThroughToolResultEvictor(),
            new TokenEstimatorCalibrator(settings),
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            new SessionToolStormStore(),
            new NoOpActiveAgentSessionContext(),
            new AgentRunContextAccessor(),
            pipeline,
            compaction,
            settings,
            logger,
            new NoOpPostTurnMemoryProcessor());

        var events = new List<string>();
        var session = AgentSession.Create("progress-test");
        var callbacks = new AgentTurnCallbacks
        {
            OnStreamEvent = streamEvent =>
            {
                events.Add(streamEvent switch
                {
                    AgentStreamEvent.RunStarted => "run-started",
                    AgentStreamEvent.ToolCallStart(var id, var name, _) => $"start:{id}:{name}",
                    AgentStreamEvent.ToolCallResult(var id, _, _) => $"result:{id}",
                    AgentStreamEvent.TextMessageContent(_, var delta) => $"text:{delta}",
                    AgentStreamEvent.RunFinished => "run-finished",
                    _ => streamEvent.GetType().Name
                });
                return Task.CompletedTask;
            }
        };

        await runtime.SendAsync(session, "run tools", null, callbacks);

        Assert.Equal("run-started", events[0]);
        Assert.Contains("start:call-1:alpha", events);
        Assert.Contains("result:call-1", events);
        Assert.Contains("start:call-2:beta", events);
        Assert.Contains("result:call-2", events);
        Assert.Contains("text:done", events);
        Assert.Equal("run-finished", events[^1]);
    }

    [Fact]
    public void FormatToolResult_IncludesToolCallIdLine()
    {
        var text = AgentRuntime.FormatToolResult(
            new AgentToolCall("abc-123", "file_read", new Dictionary<string, string> { ["path"] = "a.txt" }),
            ToolResult.Success("ok", "content"));

        Assert.StartsWith("ToolCallId: abc-123", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_UsesModelDeltaWithoutDuplicateFinalPush()
    {
        var storage = new NoOpStorage();
        var modelClient = new DeltaModelClient("hello", ["he", "llo"]);
        var settings = new AppSettings();
        var logger = new NoOpLogger();
        var (pipeline, compaction) = AgentRuntimeTestFactory.CreateMiddleware(
            new NoOpPreCompletionPipeline(), storage, new TokenEstimatorCalibrator(settings), settings, logger);
        var runtime = new AgentRuntime(
            modelClient,
            storage,
            new ScriptedToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator("test prompt"),
            new NoOpPreCompletionPipeline(),
            new PassThroughToolResultEvictor(),
            new TokenEstimatorCalibrator(settings),
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            new SessionToolStormStore(),
            new NoOpActiveAgentSessionContext(),
            new AgentRunContextAccessor(),
            pipeline,
            compaction,
            settings,
            logger,
            new NoOpPostTurnMemoryProcessor());

        var tokens = new List<string>();
        var session = AgentSession.Create("delta-test");
        await runtime.SendAsync(
            session,
            "say hello",
            null,
            new AgentTurnCallbacks
            {
                OnStreamEvent = streamEvent =>
                {
                    if (streamEvent is AgentStreamEvent.TextMessageContent(_, var delta))
                    {
                        tokens.Add(delta);
                    }

                    return Task.CompletedTask;
                }
            });

        Assert.Equal(["he", "llo"], tokens);
    }

    [Fact]
    public async Task SendAsync_InvokesToolOnCallerAsyncContext()
    {
        var storage = new NoOpStorage();
        var modelClient = new ScriptedModelClient(
            new AgentModelResponse(string.Empty, new[]
            {
                new AgentToolCall("call-1", "alpha", new Dictionary<string, string>())
            }),
            new AgentModelResponse("done", Array.Empty<AgentToolCall>()));
        var toolRouter = new ThreadCaptureToolRouter();

        var settings = new AppSettings();
        var logger = new NoOpLogger();
        var (pipeline, compaction) = AgentRuntimeTestFactory.CreateMiddleware(
            new NoOpPreCompletionPipeline(), storage, new TokenEstimatorCalibrator(settings), settings, logger);
        var runtime = new AgentRuntime(
            modelClient,
            storage,
            toolRouter,
            PromptTestHelpers.CreateStaticOrchestrator("test prompt"),
            new NoOpPreCompletionPipeline(),
            new PassThroughToolResultEvictor(),
            new TokenEstimatorCalibrator(settings),
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            new SessionToolStormStore(),
            new NoOpActiveAgentSessionContext(),
            new AgentRunContextAccessor(),
            pipeline,
            compaction,
            settings,
            logger,
            new NoOpPostTurnMemoryProcessor());

        await runtime.SendAsync(AgentSession.Create("thread-test"), "run tool");

        Assert.True(toolRouter.CapturedThreadId.HasValue);
    }

    [Fact]
    public async Task SendAsync_CanRouteMcpToolCallsThroughCompositeRouter()
    {
        var storage = new NoOpStorage();
        var modelClient = new ScriptedModelClient(
            new AgentModelResponse(string.Empty, new[]
            {
                new AgentToolCall("call-1", "mcp_demo__echo", new Dictionary<string, string> { ["argumentsJson"] = "{\"x\":1}" })
            }),
            new AgentModelResponse("done", Array.Empty<AgentToolCall>()));

        var registry = new FakeMcpRegistry();
        var composite = new Athlon.Agent.Infrastructure.CompositeToolRouter(
            Array.Empty<IAgentTool>(),
            registry,
            new AppSettings(),
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new AgentRunContextAccessor(),
            RouterTestDependencies.CreateWorkspaceGuard());
        var mcpSettings = new AppSettings();
        var logger = new NoOpLogger();
        var (pipeline, compaction) = AgentRuntimeTestFactory.CreateMiddleware(
            new NoOpPreCompletionPipeline(), storage, new TokenEstimatorCalibrator(mcpSettings), mcpSettings, logger);
        var runtime = new AgentRuntime(
            modelClient,
            storage,
            composite,
            PromptTestHelpers.CreateStaticOrchestrator("test prompt"),
            new NoOpPreCompletionPipeline(),
            new PassThroughToolResultEvictor(),
            new TokenEstimatorCalibrator(mcpSettings),
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            new SessionToolStormStore(),
            new NoOpActiveAgentSessionContext(),
            new AgentRunContextAccessor(),
            pipeline,
            compaction,
            mcpSettings,
            logger,
            new NoOpPostTurnMemoryProcessor());

        await runtime.SendAsync(AgentSession.Create("mcp-route-test"), "call mcp");

        Assert.Equal(("demo", "echo"), registry.LastInvocation);
    }

    private sealed class ScriptedModelClient(params AgentModelResponse[] responses) : IAgentModelClient
    {
        private int _index;

        public Task<AgentModelResponse> CompleteAsync(AgentModelRequest request, Func<string, Task>? onTextDelta = null, Func<string, Task>? onReasoningDelta = null, Func<StreamingToolCallDelta, Task>? onToolCallDelta = null, CancellationToken cancellationToken = default)
        {
            if (_index >= responses.Length)
            {
                throw new InvalidOperationException("No more scripted responses.");
            }

            return Task.FromResult(responses[_index++]);
        }
    }

    private sealed class DeltaModelClient(string content, IReadOnlyList<string> deltas) : IAgentModelClient
    {
        private bool _done;

        public async Task<AgentModelResponse> CompleteAsync(AgentModelRequest request, Func<string, Task>? onTextDelta = null, Func<string, Task>? onReasoningDelta = null, Func<StreamingToolCallDelta, Task>? onToolCallDelta = null, CancellationToken cancellationToken = default)
        {
            if (_done)
            {
                throw new InvalidOperationException("Only one response expected.");
            }

            _done = true;
            if (onTextDelta is not null)
            {
                foreach (var delta in deltas)
                {
                    await onTextDelta(delta);
                }
            }

            return new AgentModelResponse(content, Array.Empty<AgentToolCall>());
        }
    }

    private sealed class ScriptedToolRouter : IToolRouter
    {
        public IReadOnlyList<ToolDefinition> ListTools() =>
            new[] { new ToolDefinition("alpha", "a", new Dictionary<string, string>()), new ToolDefinition("beta", "b", new Dictionary<string, string>()) };

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success($"ran {invocation.ToolName}"));
    }

    private sealed class ThreadCaptureToolRouter : IToolRouter
    {
        public int? CapturedThreadId { get; private set; }
        public bool CapturedOnThreadPool { get; private set; }

        public IReadOnlyList<ToolDefinition> ListTools() =>
            new[] { new ToolDefinition("alpha", "a", new Dictionary<string, string>()) };

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            CapturedThreadId = Environment.CurrentManagedThreadId;
            CapturedOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            return Task.FromResult(ToolResult.Success("ok"));
        }
    }

    private sealed class FakeMcpRegistry : Athlon.Agent.Infrastructure.IMcpRegistry
    {
        public (string server, string tool)? LastInvocation { get; private set; }

        public IReadOnlyList<Athlon.Agent.Mcp.McpServerStatus> GetStatuses() => Array.Empty<Athlon.Agent.Mcp.McpServerStatus>();

        public IReadOnlyList<ToolDefinition> ListToolDefinitions() =>
            new[] { new ToolDefinition("mcp_demo__echo", "echo", new Dictionary<string, string> { ["argumentsJson"] = "args" }, Source: "mcp") };

        public IReadOnlyList<McpCatalogEntry> ListCatalogEntries() =>
            [new McpCatalogEntry("demo", "echo", "mcp_demo__echo", "echo", "{}")];

        public int CatalogVersion => 0;
        public int CatalogCount => ListCatalogEntries().Count;
        public int CatalogSchemaCharCount => ListCatalogEntries().Sum(entry => entry.Description.Length + entry.InputSchemaJson.Length);
        public IReadOnlyList<McpSearchIndex.SearchResult> SearchCatalog(string query, int topK, double minScore, string? serverName = null) =>
            McpSearchIndex.Search(ListCatalogEntries(), query, topK, minScore);

        public Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ToolResult> InvokeAsync(string serverName, string toolName, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken = default)
        {
            LastInvocation = (serverName, toolName);
            return Task.FromResult(ToolResult.Success("ok", "{\"ok\":true}"));
        }
    }

    private sealed class StaticPromptBuilder : IAgentEnvironmentPromptBuilder
    {
        public string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools) => "test prompt";
    }

    private sealed class NoOpPreCompletionPipeline : IPreCompletionPipeline
    {
        public Task<AgentSession> RunAsync(
            AgentSession session,
            PreCompletionOptions? options = null,
            CompactionRuntimeContext? runtimeContext = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(session);
    }

    private sealed class PassThroughToolResultEvictor : IToolResultEvictor
    {
        public Task<string> EvictIfNeededAsync(
            string sessionId,
            AgentToolCall toolCall,
            ToolResult result,
            string formattedToolContent,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(formattedToolContent);
    }

    private sealed class NoOpStorage : IFileStorageService
    {
        public string RootPath => "/tmp";
        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<AgentSession?>(null);
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult("/tmp/t.jsonl");
        public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) => Task.FromResult("/tmp/evicted.txt");
        public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
        public Task ReplaceConversationDisplayAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
