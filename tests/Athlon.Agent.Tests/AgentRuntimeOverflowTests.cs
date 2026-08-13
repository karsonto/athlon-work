using System.Net.Http;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class AgentRuntimeOverflowTests
{
    [Fact]
    public async Task SendAsync_ContextOverflow_RetryUsesRequestHistoryHygiene()
    {
        var compactor = new PrefixDroppingConversationCompactor();
        var huge = new string('y', 50_000);
        var settings = CreateOverflowSettings();
        var modelClient = new OverflowCapturingModelClient();
        var runtime = CreateRuntime(compactor, modelClient, settings);

        var session = AgentSession.Create("overflow-hygiene");
        session = session.WithMessage(ChatMessage.Create(MessageRole.User, new string('x', 50_000)));
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

        Assert.Equal(1, compactor.ForceCallCount);
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
        var compactor = new PrefixDroppingConversationCompactor();
        var settings = CreateOverflowSettings();
        var modelClient = new OverflowThenSuccessModelClient();
        var runtime = CreateRuntime(compactor, modelClient, settings);

        var session = AgentSession.Create("overflow");
        session = session.WithMessage(ChatMessage.Create(MessageRole.User, new string('x', 50_000)));
        var result = await runtime.SendAsync(session, "hello");

        Assert.Equal(1, compactor.ForceCallCount);
        Assert.Equal(2, modelClient.CallCount);
        Assert.Contains(result.Messages, message => message.Role == MessageRole.Assistant);
    }

    [Fact]
    public async Task SendAsync_ContextOverflow_SkipsRetryWhenPayloadNotReduced()
    {
        var compactor = new NonReducingConversationCompactor();
        var settings = CreateOverflowSettings();
        var modelClient = new OverflowThenSuccessModelClient();
        var runtime = CreateRuntime(compactor, modelClient, settings);

        var skipped = new List<AgentStreamEvent>();
        var session = AgentSession.Create("overflow-skip");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => runtime.SendAsync(
            session,
            "hello",
            callbacks: new AgentTurnCallbacks
            {
                OnStreamEvent = streamEvent =>
                {
                    skipped.Add(streamEvent);
                    return Task.CompletedTask;
                }
            }));

        Assert.Equal(1, compactor.ForceCallCount);
        Assert.Equal(1, modelClient.CallCount);
        Assert.Contains("context_length", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(skipped, item => item is AgentStreamEvent.OverflowRetrySkipped);
        Assert.Contains(skipped, item => item is AgentStreamEvent.ContextBudgetUpdated updated
            && updated.Pressure == ContextPressureLevel.Overflow);
    }

    private static AppSettings CreateOverflowSettings() =>
        new()
        {
            ContextCompaction = new ContextCompactionSettings
            {
                TriggerMessages = 100,
                TriggerTokens = 1_000_000
            }
        };

    private static AgentRuntime CreateRuntime(
        IConversationCompactor compactor,
        IAgentModelClient modelClient,
        AppSettings settings)
    {
        var pipeline = new PreCompletionPipeline(
            compactor,
            new TruncateArgsService(),
            settings,
            new NoOpLogger());
        var storage = new NoOpStorage();
        var logger = new NoOpLogger();
        var (turnPipeline, compaction) = AgentRuntimeTestFactory.CreateMiddleware(
            pipeline, storage, new TokenEstimatorCalibrator(settings), settings, logger);
        return new AgentRuntime(
            modelClient,
            storage,
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            new PassThroughToolResultEvictor(),
            new TokenEstimatorCalibrator(settings),
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            new NoOpActiveAgentSessionContext(),
            new AgentRunContextAccessor(),
            turnPipeline,
            compaction,
            settings,
            logger);
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

    /// <summary>
    /// Drops history before the last user turn (and its preceding tool batch) so overflow retry
    /// payload is strictly smaller than the failed request.
    /// </summary>
    private sealed class PrefixDroppingConversationCompactor : IConversationCompactor
    {
        public int ForceCallCount { get; private set; }

        public Task<ConversationCompactResult> CompactIfNeededAsync(
            AgentSession session,
            CompactionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!request.Force)
            {
                return Task.FromResult(new ConversationCompactResult(session, false));
            }

            ForceCallCount++;
            var messages = session.Messages;
            if (messages.Count == 0)
            {
                return Task.FromResult(new ConversationCompactResult(session, false));
            }

            var keepStart = messages.Count - 1;
            while (keepStart > 0 && messages[keepStart - 1].Role == MessageRole.Tool)
            {
                keepStart--;
            }

            if (keepStart > 0
                && messages[keepStart - 1].Role == MessageRole.Assistant
                && !string.IsNullOrWhiteSpace(messages[keepStart - 1].ToolCallsJson))
            {
                keepStart--;
            }

            var summary = SummaryMessageBuilder.CreateSummaryPlaceholder("forced summary", null);
            var kept = new List<ChatMessage> { summary };
            kept.AddRange(messages.Skip(keepStart));
            return Task.FromResult(new ConversationCompactResult(session.WithMessages(kept), true));
        }
    }

    private sealed class NonReducingConversationCompactor : IConversationCompactor
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

            return Task.FromResult(new ConversationCompactResult(session, false));
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
