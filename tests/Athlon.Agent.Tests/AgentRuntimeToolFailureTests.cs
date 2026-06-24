using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class AgentRuntimeToolFailureTests
{
    [Fact]
    public async Task SendAsync_ToolThrows_ReturnsFailureToModelAndContinuesTurn()
    {
        var storage = new NoOpStorage();
        var modelClient = new ScriptedModelClient(
            new AgentModelResponse(string.Empty, new[]
            {
                new AgentToolCall("call-1", "boom", new Dictionary<string, string>())
            }),
            new AgentModelResponse("recovered after tool failure", Array.Empty<AgentToolCall>()));

        var settings = new AppSettings();
        var logger = new NoOpLogger();
        var (pipeline, compaction) = AgentRuntimeTestFactory.CreateMiddleware(
            new NoOpPreCompletionPipeline(), storage, new TokenEstimatorCalibrator(settings), settings, logger);
        var runtime = new AgentRuntime(
            modelClient,
            storage,
            new ThrowingToolRouter(),
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

        var session = AgentSession.Create("tool-failure-test");
        var result = await runtime.SendAsync(session, "run tool", null);

        var toolMessage = result.Messages.LastOrDefault(message => message.Role == MessageRole.Tool);
        Assert.NotNull(toolMessage);
        Assert.Contains("failed", toolMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cmd.exe", toolMessage.Content, StringComparison.OrdinalIgnoreCase);

        var assistant = result.Messages.LastOrDefault(message => message.Role == MessageRole.Assistant);
        Assert.NotNull(assistant);
        Assert.Contains("recovered", assistant.Content, StringComparison.Ordinal);
    }

    private sealed class ThrowingToolRouter : IToolRouter
    {
        public IReadOnlyList<ToolDefinition> ListTools() =>
            new[] { new ToolDefinition("boom", "throws", new Dictionary<string, string>()) };

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "An error occurred trying to start process 'cmd.exe' with working directory 'C:\\missing'.");
    }

    private sealed class ScriptedModelClient(params AgentModelResponse[] responses) : IAgentModelClient
    {
        private int _index;

        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            if (_index >= responses.Length)
            {
                throw new InvalidOperationException("No more scripted responses.");
            }

            return Task.FromResult(responses[_index++]);
        }
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
        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
