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
