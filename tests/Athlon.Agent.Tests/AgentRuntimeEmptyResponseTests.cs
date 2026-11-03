using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

/// <summary>
/// A final round with no tool calls and no text used to end the turn silently. These tests
/// pin the guard: one retry with a transient nudge, then a visible degradation.
/// </summary>
public sealed class AgentRuntimeEmptyResponseTests
{
    [Fact]
    public async Task SendAsync_EmptyFinalRound_RetriesOnceThenUsesSecondAnswer()
    {
        var modelClient = new RecordingModelClient(
            Response(string.Empty),
            Response("这里是真正的回答。"));

        var result = await RunAsync(modelClient);

        Assert.Equal(2, modelClient.Requests.Count);

        // The retry must carry the nudge exactly once, as a transient trailing user message.
        Assert.DoesNotContain(FirstRequestText(modelClient, 0), "previous reply was empty", StringComparison.Ordinal);
        Assert.Contains(FirstRequestText(modelClient, 1), "previous reply was empty", StringComparison.Ordinal);

        var assistant = LastAssistant(result);
        Assert.Equal("这里是真正的回答。", assistant.Content);
        Assert.Single(result.Messages, message => message.Role == MessageRole.Assistant);
    }

    [Fact]
    public async Task SendAsync_EmptyFinalRoundsTwice_WritesVisibleFallback()
    {
        var modelClient = new RecordingModelClient(
            Response(string.Empty),
            Response("   "));

        var result = await RunAsync(modelClient);

        // One retry, never an unbounded loop.
        Assert.Equal(2, modelClient.Requests.Count);

        var assistant = LastAssistant(result);
        Assert.False(string.IsNullOrWhiteSpace(assistant.Content));
        Assert.StartsWith("（本轮未产生可显示的回复", assistant.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_WhitespaceReasoningOnly_StillCountsAsEmpty()
    {
        var modelClient = new RecordingModelClient(
            new AgentModelResponse(string.Empty, Array.Empty<AgentToolCall>(), ReasoningContent: "thinking only"),
            Response("done"));

        var result = await RunAsync(modelClient);

        Assert.Equal(2, modelClient.Requests.Count);
        Assert.Equal("done", LastAssistant(result).Content);
    }

    [Fact]
    public async Task SendAsync_NonEmptyAnswer_DoesNotRetry()
    {
        var modelClient = new RecordingModelClient(Response("直接回答"));

        var result = await RunAsync(modelClient);

        Assert.Single(modelClient.Requests);
        Assert.Equal("直接回答", LastAssistant(result).Content);
    }

    private static AgentModelResponse Response(string content) =>
        new(content, Array.Empty<AgentToolCall>());

    private static ChatMessage LastAssistant(AgentSession session) =>
        session.Messages.Last(message => message.Role == MessageRole.Assistant);

    private static string FirstRequestText(RecordingModelClient client, int index) =>
        string.Join(
            "\n",
            client.Requests[index].Messages
                .Where(message => message.Content is string)
                .Select(message => (string)message.Content));

    private static async Task<AgentSession> RunAsync(RecordingModelClient modelClient)
    {
        var settings = new AppSettings();
        var logger = new NoOpLogger();
        var storage = new NoOpStorage();
        var (pipeline, compaction) = AgentRuntimeTestFactory.CreateMiddleware(
            new NoOpPreCompletionPipeline(), storage, new TokenEstimatorCalibrator(settings), settings, logger);
        var runtime = AgentRuntimeTestFactory.CreateRuntimeFromMiddleware(
            modelClient,
            storage,
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator("test prompt"),
            new PassThroughToolResultEvictor(),
            new TokenEstimatorCalibrator(settings),
            new NoOpActiveAgentSessionContext(),
            new AgentRunContextAccessor(),
            pipeline,
            compaction,
            settings,
            logger);

        return await runtime.SendAsync(AgentSession.Create("empty-response-test"), "hi", null);
    }

    private sealed class RecordingModelClient(params AgentModelResponse[] responses) : IAgentModelClient
    {
        private int _index;

        public List<AgentModelRequest> Requests { get; } = [];

        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
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
}
