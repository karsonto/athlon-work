using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Tests;

public sealed class AgentRuntimeSubAgentContextTests
{
    [Fact]
    public async Task SendAsync_ReusesExistingSubAgentRunContext()
    {
        var accessor = new AgentRunContextAccessor();
        var childRouter = new MarkerToolRouter("child");
        var rootRouter = new MarkerToolRouter("root");
        var childPrompt = new MarkerPrompt("child-sys");
        var rootPrompt = new MarkerPrompt("root-sys");
        var storage = new NoOpStorage();
        var settings = new AppSettings();
        var logger = new LoopNoOpAppLogger();
        var (pipeline, compaction) = AgentRuntimeTestFactory.CreateMiddleware(
            new NoOpPreCompletionPipeline(),
            storage,
            new NoOpTokenEstimator(),
            settings,
            logger);

        var runtime = new AgentRuntime(
            new FixedModelClient("done"),
            storage,
            rootRouter,
            rootPrompt,
            new NoOpPreCompletionPipeline(),
            new NoOpToolResultEvictor(),
            new NoOpTokenEstimator(),
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            new SessionToolStormStore(),
            new NoOpActiveAgentSessionContext(),
            accessor,
            pipeline,
            compaction,
            settings,
            logger,
            new NoOpPostTurnMemoryProcessor());

        var session = AgentSession.Create("sub-1");
        var child = AgentRunContext.CreateRoot(
                session,
                "run-root",
                rootRouter,
                rootPrompt,
                [])
            .CreateChild(
                session.Id,
                childRouter,
                childPrompt,
                "researcher",
                new AgentLoopOptions { MaxModelToolRounds = 2 },
                workspaceRoot: null,
                ignorePatterns: []);

        AgentRunContext? seen = null;
        using (accessor.Push(child))
        {
            await runtime.SendAsync(
                session,
                "hello",
                callbacks: new AgentTurnCallbacks
                {
                    OnSessionUpdated = _ =>
                    {
                        seen = accessor.Current;
                        return Task.CompletedTask;
                    }
                });
        }

        Assert.NotNull(seen);
        Assert.Equal(AgentRunKind.SubAgent, seen.Kind);
        Assert.Same(childRouter, seen.ToolRouter);
        Assert.Same(childPrompt, seen.PromptOrchestrator);
        Assert.Equal(2, seen.LoopOptions?.MaxModelToolRounds);
        Assert.Equal("child-sys", childPrompt.LastPrepared);
        Assert.Null(rootPrompt.LastPrepared);
    }

    private sealed class MarkerToolRouter(string name) : IToolRouter
    {
        public string Name { get; } = name;

        public IReadOnlyList<ToolDefinition> ListTools() =>
            [new ToolDefinition($"tool-{name}", name, ToolSchema.Object().Build())];

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class MarkerPrompt(string text) : ISystemPromptOrchestrator
    {
        public string? LastPrepared { get; private set; }

        public FrozenSystemPrompt PrepareForTurn(AgentSession session, IReadOnlyList<ToolDefinition> tools)
        {
            LastPrepared = text;
            return new FrozenSystemPrompt(text);
        }

        public string? BuildRuntimeContext(AgentSession session, IReadOnlyList<ToolDefinition> tools) => null;

        public string BuildForReasoningIteration(
            FrozenSystemPrompt frozen,
            AgentSession session,
            IReadOnlyList<ToolDefinition> tools) => frozen.Text;
    }

    private sealed class FixedModelClient(string content) : IAgentModelClient
    {
        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentModelResponse(content, Array.Empty<AgentToolCall>()));
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
        public double GetMultiplier(string sessionId) => 1.0;

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
