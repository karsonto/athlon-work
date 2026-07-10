using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Tests;

public sealed class AgentLoopOptionsTests
{
    [Fact]
    public async Task SendAsync_StopsAfterMaxModelToolRounds_WithoutExecutingTools()
    {
        var storage = new NoOpStorage();
        var model = new LoopTestModelClient();
        var settings = new AppSettings();
        var logger = new LoopNoOpAppLogger();
        var (pipeline, compaction) = AgentRuntimeTestFactory.CreateMiddleware(
            new NoOpPreCompletionPipeline(),
            storage,
            new NoOpTokenEstimator(),
            settings,
            logger);
        var runtime = new AgentRuntime(
            model,
            storage,
            new LoopTestToolRouter(),
            new LoopTestPrompt(),
            new NoOpPreCompletionPipeline(),
            new NoOpToolResultEvictor(),
            new NoOpTokenEstimator(),
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

        var session = AgentSession.Create("loop-test");
        using (AgentLoopOptionsScope.Enter(new AgentLoopOptions { MaxModelToolRounds = 1 }))
        {
            session = await runtime.SendAsync(session, "hi");
        }

        Assert.Equal(1, model.CompleteCount);
        Assert.DoesNotContain(session.Messages, message => message.Role == MessageRole.Tool);
        var lastAssistant = session.Messages.Last(message => message.Role == MessageRole.Assistant);
        Assert.Contains("calling tool", lastAssistant.Content);
    }

    private sealed class LoopTestModelClient : IAgentModelClient
    {
        public int CompleteCount { get; private set; }

        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            if (CompleteCount == 1)
            {
                return Task.FromResult(new AgentModelResponse(
                    "calling tool",
                    [new AgentToolCall("tc1", "noop", new Dictionary<string, string>())]));
            }

            return Task.FromResult(new AgentModelResponse("done round-2", Array.Empty<AgentToolCall>()));
        }
    }

    private sealed class LoopTestToolRouter : IToolRouter
    {
        public IReadOnlyList<ToolDefinition> ListTools() =>
            [new ToolDefinition("noop", "noop", ToolSchema.Object().Build())];

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ran", "tool-output"));
    }

    private sealed class LoopTestPrompt : Athlon.Agent.Core.Prompt.ISystemPromptOrchestrator
    {
        public Athlon.Agent.Core.Prompt.FrozenSystemPrompt PrepareForTurn(AgentSession session, IReadOnlyList<ToolDefinition> tools) =>
            new("sys");

        public string? BuildRuntimeContext(AgentSession session, IReadOnlyList<ToolDefinition> tools) => null;

        public string BuildForReasoningIteration(
            Athlon.Agent.Core.Prompt.FrozenSystemPrompt frozen,
            AgentSession session,
            IReadOnlyList<ToolDefinition> tools) => frozen.Text;
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

    private sealed class NoOpToolResultEvictor : IToolResultEvictor
    {
        public Task<string> EvictIfNeededAsync(
            string sessionId,
            AgentToolCall toolCall,
            ToolResult result,
            string content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(content);
    }

    private sealed class NoOpTokenEstimator : ITokenEstimatorCalibrator
    {
        public double GetMultiplier(string sessionId) => 1;
        public void Observe(string sessionId, int estimatedPromptTokens, int? actualPromptTokens) { }
    }

    private sealed class LoopNoOpAppLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
