using System.Net.Http;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class AgentRuntimeOverflowTests
{
    [Fact]
    public async Task SendAsync_ContextOverflow_ForcesCompactAndRetriesOnce()
    {
        var compactor = new CountingConversationCompactor();
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                TriggerMessages = 100,
                TriggerTokens = 1_000_000
            }
        };

        var pipeline = new PreCompletionPipeline(compactor, new NoOpLogger());

        var modelClient = new OverflowThenSuccessModelClient();
        var runtime = new AgentRuntime(
            modelClient,
            new NoOpStorage(),
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            pipeline,
            new PassThroughToolResultEvictor(),
            new NoOpActiveAgentSessionContext(),
            new AppSettings(),
            new NoOpLogger());

        var session = AgentSession.Create("overflow");
        var result = await runtime.SendAsync(session, "hello");

        Assert.Equal(1, compactor.ForceCallCount);
        Assert.Equal(2, modelClient.CallCount);
        Assert.Contains(result.Messages, message => message.Role == MessageRole.Assistant);
    }

    private sealed class OverflowThenSuccessModelClient : IAgentModelClient
    {
        public int CallCount { get; private set; }

        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new HttpRequestException("context_length exceeded");
            }

            return Task.FromResult(new AgentModelResponse("done", Array.Empty<AgentToolCall>()));
        }
    }

    private sealed class CountingConversationCompactor : IConversationCompactor
    {
        public int ForceCallCount { get; private set; }

        public Task<ConversationCompactResult> CompactIfNeededAsync(
            AgentSession session,
            CompactionKind kind,
            bool force,
            bool emitAudit,
            CancellationToken cancellationToken = default)
        {
            if (force)
            {
                ForceCallCount++;
            }

            if (!force)
            {
                return Task.FromResult(new ConversationCompactResult(session, false));
            }

            var summary = SummaryMessageBuilder.CreateSummaryPlaceholder("forced summary", null);
            return Task.FromResult(new ConversationCompactResult(session.WithMessage(summary), true));
        }
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

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
