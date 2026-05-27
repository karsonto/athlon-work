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
            new StaticPromptBuilder(),
            new NoOpPreCompletionPipeline(),
            new NoOpAutoCompactService(),
            new NoOpActiveAgentSessionContext(),
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

        await runtime.SendAsync(session, "run tools", callbacks);

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

    private sealed class ScriptedModelClient(params AgentModelResponse[] responses) : IAgentModelClient
    {
        private int _index;

        public Task<AgentModelResponse> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
        {
            if (_index >= responses.Length)
            {
                throw new InvalidOperationException("No more scripted responses.");
            }

            return Task.FromResult(responses[_index++]);
        }
    }

    private sealed class ScriptedToolRouter : IToolRouter
    {
        public IReadOnlyList<ToolDefinition> ListTools() =>
            new[] { new ToolDefinition("alpha", "a", new Dictionary<string, string>()), new ToolDefinition("beta", "b", new Dictionary<string, string>()) };

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success($"ran {invocation.ToolName}"));
    }

    private sealed class StaticPromptBuilder : IAgentEnvironmentPromptBuilder
    {
        public string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools) => "test prompt";
    }

    private sealed class NoOpPreCompletionPipeline : IPreCompletionPipeline
    {
        public Task<AgentSession> RunAsync(AgentSession session, CancellationToken cancellationToken = default) =>
            Task.FromResult(session);
    }

    private sealed class NoOpAutoCompactService : IAutoCompactService
    {
        public Task<AgentSession> CompactAsync(AgentSession session, CancellationToken cancellationToken = default) =>
            Task.FromResult(session);
    }

    private sealed class NoOpStorage : IFileStorageService
    {
        public string RootPath => "/tmp";
        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<AgentSession?>(null);
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult("/tmp/t.jsonl");
        public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
    }

    private sealed class NoOpActiveAgentSessionContext : IActiveAgentSessionContext
    {
        public string? SessionId { get; private set; }

        public void SetSession(string? sessionId) => SessionId = sessionId;
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
