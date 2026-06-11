using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;



namespace Athlon.Agent.Tests;



public sealed class AgentRuntimeCompactionTests

{

    [Fact]

    public async Task SendAsync_NotifiesCompactionAuditFromPipeline()

    {

        var compactionAudit = CompactionMessageContent.CreateCompactionMessage(

            CompactionMessageContent.CreateConversationCompact(1000, 800, 2, "fake.jsonl", "summary"));

        var pipeline = new InjectingPreCompletionPipeline(compactionAudit);

        var storage = new CapturingStorage();

        var runtime = new AgentRuntime(

            new FakeModelClient("done"),

            storage,

            new NoOpToolRouter(),

            PromptTestHelpers.CreateStaticOrchestrator(),

            pipeline,

            new PassThroughToolResultEvictor(),

            new TokenEstimatorCalibrator(new AppSettings()),

            new SessionUsageAccumulator(),

            new PromptPressureStore(),

            new SessionToolStormStore(),

            new NoOpActiveAgentSessionContext(),

            new AppSettings(),

            new NoOpLogger(),
            new NoOpPostTurnMemoryProcessor());

        ChatMessage? notified = null;
        AgentSession? sessionAfterCompact = null;

        var session = AgentSession.Create("compaction-notify");

        await runtime.SendAsync(

            session,

            "hello",

            null,

            new AgentTurnCallbacks

            {

                OnSessionUpdated = updatedSession =>

                {

                    sessionAfterCompact = updatedSession;

                    return Task.CompletedTask;

                },

                OnStreamEvent = streamEvent =>

                {

                    if (streamEvent is AgentStreamEvent.ChatMessageAppended(var message)

                        && message.Role == MessageRole.Compaction)

                    {

                        notified = message;

                    }



                    return Task.CompletedTask;

                }

            });



        Assert.NotNull(notified);

        Assert.Contains("conversationcompact", notified.Content, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(sessionAfterCompact);

        Assert.Contains(sessionAfterCompact.Messages, message => message.Role == MessageRole.Compaction);

        Assert.Contains(storage.PersistedMessages, message => message.Role == MessageRole.Compaction);

    }



    private sealed class InjectingPreCompletionPipeline(ChatMessage compactionAudit) : IPreCompletionPipeline

    {

        public Task<AgentSession> RunAsync(

            AgentSession session,

            PreCompletionOptions? options = null,

            CompactionRuntimeContext? runtimeContext = null,

            CancellationToken cancellationToken = default) =>

            Task.FromResult(session.WithMessage(compactionAudit));

    }



    private sealed class CapturingStorage : IFileStorageService

    {

        public List<ChatMessage> PersistedMessages { get; } = new();

        public string RootPath => "/tmp";



        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default) =>

            Task.CompletedTask;



        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>

            Task.FromResult<AgentSession?>(null);



        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>

            Task.CompletedTask;



        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) =>

            Task.CompletedTask;



        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) =>

            Task.FromResult("/tmp/t.jsonl");



        public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) =>

            Task.FromResult("/tmp/evicted.txt");



        public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default)

        {

            PersistedMessages.Add(message);

            return Task.CompletedTask;

        }

        public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>(PersistedMessages.ToArray());

        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) =>

            Task.CompletedTask;



        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) =>

            Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());



        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>

            Task.CompletedTask;



        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>

            Task.FromResult(new AppSettings());

    }



    private sealed class FakeModelClient(string content) : IAgentModelClient

    {

        public Task<AgentModelResponse> CompleteAsync(

            AgentModelRequest request,

            Func<string, Task>? onTextDelta = null,

            Func<string, Task>? onReasoningDelta = null,

            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,

            CancellationToken cancellationToken = default) =>

            Task.FromResult(new AgentModelResponse(content, Array.Empty<AgentToolCall>()));

    }



    private sealed class NoOpToolRouter : IToolRouter

    {

        public IReadOnlyList<ToolDefinition> ListTools() => Array.Empty<ToolDefinition>();



        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>

            Task.FromResult(ToolResult.Success("ok"));

    }



    private sealed class StaticPromptBuilder : IAgentEnvironmentPromptBuilder

    {

        public string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools) => "prompt";

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



    private sealed class NoOpLogger : IAppLogger

    {

        public void Debug(string messageTemplate, params object[] values) { }

        public void Information(string messageTemplate, params object[] values) { }

        public void Warning(string messageTemplate, params object[] values) { }

        public void Error(Exception exception, string messageTemplate, params object[] values) { }

        public IAppLogger ForContext(string sourceContext) => this;

    }

}


