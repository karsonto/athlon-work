using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

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
        var runtime = new AgentRuntime(
            modelClient,
            storage,
            toolRouter,
            PromptTestHelpers.CreateStaticOrchestrator("test prompt"),
            new NoOpPreCompletionPipeline(),
            new PassThroughToolResultEvictor(),
            new NoOpActiveAgentSessionContext(),
            new AppSettings(),
            new NoOpLogger());

        var events = new List<string>();
        var session = AgentSession.Create("progress-test");
        var callbacks = new AgentTurnCallbacks
        {
            OnToolStarted = call =>
            {
                events.Add($"start:{call.Id}:{call.Name}");
                return Task.CompletedTask;
            },
            OnMessage = message =>
            {
                events.Add($"message:{message.Role}");
                return Task.CompletedTask;
            }
        };

        await runtime.SendAsync(session, "run tools", null, callbacks);

        Assert.Equal(
            new[]
            {
                "start:call-1:alpha",
                "message:Tool",
                "start:call-2:beta",
                "message:Tool",
                "message:Assistant"
            },
            events);
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
        var runtime = new AgentRuntime(
            modelClient,
            storage,
            new ScriptedToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator("test prompt"),
            new NoOpPreCompletionPipeline(),
            new PassThroughToolResultEvictor(),
            new NoOpActiveAgentSessionContext(),
            new AppSettings(),
            new NoOpLogger());

        var tokens = new List<string>();
        var session = AgentSession.Create("delta-test");
        await runtime.SendAsync(
            session,
            "say hello",
            null,
            new AgentTurnCallbacks
            {
                OnAssistantTextDelta = token =>
                {
                    tokens.Add(token);
                    return Task.CompletedTask;
                }
            });

        Assert.Equal(["he", "llo"], tokens);
    }

    [Fact]
    public async Task SendAsync_RunsToolInvocationOnThreadPoolThread()
    {
        var storage = new NoOpStorage();
        var modelClient = new ScriptedModelClient(
            new AgentModelResponse(string.Empty, new[]
            {
                new AgentToolCall("call-1", "alpha", new Dictionary<string, string>())
            }),
            new AgentModelResponse("done", Array.Empty<AgentToolCall>()));
        var toolRouter = new ThreadCaptureToolRouter();

        var runtime = new AgentRuntime(
            modelClient,
            storage,
            toolRouter,
            PromptTestHelpers.CreateStaticOrchestrator("test prompt"),
            new NoOpPreCompletionPipeline(),
            new PassThroughToolResultEvictor(),
            new NoOpActiveAgentSessionContext(),
            new AppSettings(),
            new NoOpLogger());

        await runtime.SendAsync(AgentSession.Create("thread-test"), "run tool");

        Assert.True(toolRouter.CapturedThreadId.HasValue);
        Assert.True(toolRouter.CapturedOnThreadPool);
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
        var composite = new Athlon.Agent.Infrastructure.CompositeToolRouter(Array.Empty<IAgentTool>(), registry);
        var runtime = new AgentRuntime(
            modelClient,
            storage,
            composite,
            PromptTestHelpers.CreateStaticOrchestrator("test prompt"),
            new NoOpPreCompletionPipeline(),
            new PassThroughToolResultEvictor(),
            new NoOpActiveAgentSessionContext(),
            new AppSettings(),
            new NoOpLogger());

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
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
