using System.Net.Http;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class AgentRuntimeOverflowTests
{
    [Fact]
    public async Task SendAsync_ContextOverflow_RetryUsesRequestHistoryHygiene()
    {
        var compactor = new CountingConversationCompactor();
        var huge = new string('y', 50_000);
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                TriggerMessages = 100,
                TriggerTokens = 1_000_000
            }
        };

        var pipeline = new PreCompletionPipeline(
            compactor,
            new TruncateArgsService(),
            settings,
            new NoOpLogger());

        var modelClient = new OverflowCapturingModelClient();
        var runtime = new AgentRuntime(
            modelClient,
            new NoOpStorage(),
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            pipeline,
            new PassThroughToolResultEvictor(),
            new TokenEstimatorCalibrator(settings),
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            new SessionToolStormStore(),
            new NoOpActiveAgentSessionContext(),
            settings,
            new NoOpLogger(),
            new NoOpPostTurnMemoryProcessor());

        var session = AgentSession.Create("overflow-hygiene");
        session = session.WithMessage(ChatMessage.Create(MessageRole.User, "hello"));
        session = session.WithMessage(ChatMessage.CreateWithId(
            "a1",
            MessageRole.Assistant,
            string.Empty,
            null,
            [new AgentToolCall("tc1", "file_read", new Dictionary<string, string>())]));
        session = session.WithMessage(ChatMessage.Create(
            MessageRole.Tool,
            AgentRuntime.FormatToolResult(
                new AgentToolCall("tc1", "file_read", new Dictionary<string, string>()),
                ToolResult.Success("ok", huge))));

        await runtime.SendAsync(session, "continue");

        Assert.Equal(2, modelClient.CallCount);
        Assert.NotNull(modelClient.RetryRequest);
        Assert.Contains(
            modelClient.RetryRequest!.Messages,
            message => message.Role == "tool"
                && message.Content is string content
                && content.Contains("[cache hygiene:", StringComparison.Ordinal));
    }

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

        var pipeline = new PreCompletionPipeline(
            compactor,
            new TruncateArgsService(),
            settings,
            new NoOpLogger());

        var modelClient = new OverflowThenSuccessModelClient();
        var runtime = new AgentRuntime(
            modelClient,
            new NoOpStorage(),
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            pipeline,
            new PassThroughToolResultEvictor(),
            new TokenEstimatorCalibrator(settings),
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            new SessionToolStormStore(),
            new NoOpActiveAgentSessionContext(),
            settings,
            new NoOpLogger(),
            new NoOpPostTurnMemoryProcessor());

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

    private sealed class OverflowCapturingModelClient : IAgentModelClient
    {
        public int CallCount { get; private set; }
        public AgentModelRequest? RetryRequest { get; private set; }

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

            RetryRequest = request;
            return Task.FromResult(new AgentModelResponse("done", Array.Empty<AgentToolCall>()));
        }
    }

    private sealed class CountingConversationCompactor : IConversationCompactor
    {
        public int ForceCallCount { get; private set; }

        public Task<ConversationCompactResult> CompactIfNeededAsync(
            AgentSession session,
            CompactionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Force)
            {
                ForceCallCount++;
            }

            if (!request.Force)
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

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
