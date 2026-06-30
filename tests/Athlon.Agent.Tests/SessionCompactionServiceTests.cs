using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class SessionCompactionServiceTests
{
    [Fact]
    public async Task CompactAsync_SingleMessage_StillCompresses()
    {
        var service = CreateService();
        var session = AgentSession.Create("short").WithMessages(
        [
            ChatMessage.Create(MessageRole.User, "only one message")
        ]);

        var result = await service.CompactAsync(session);

        Assert.True(result.Compacted);
        Assert.Contains(result.Session.Messages, message => message.Role == MessageRole.Compaction);
    }

    [Fact]
    public async Task CompactAsync_LongConversation_CompressesWithManualStrategy()
    {
        var service = CreateService();
        var session = AgentSession.Create("manual").WithMessages(
        [
            ChatMessage.Create(MessageRole.User, "one"),
            ChatMessage.Create(MessageRole.Assistant, "two"),
            ChatMessage.Create(MessageRole.User, "three"),
            ChatMessage.Create(MessageRole.Assistant, "four"),
            ChatMessage.Create(MessageRole.User, "five")
        ]);

        var result = await service.CompactAsync(session);

        Assert.True(result.Compacted);
        Assert.Contains(result.Session.Messages, message => message.Role == MessageRole.Compaction);
        var audit = result.Session.Messages.First(message => message.Role == MessageRole.Compaction);
        Assert.Contains("manual_compact", audit.Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Session.Messages.Count < session.Messages.Count);
    }

    private static SessionCompactionService CreateService()
    {
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                Enabled = false,
                TriggerMessages = 2,
                KeepMessages = 1,
                SummaryMaxTokens = 512,
                MaxConversationCharsForSummary = 10_000
            }
        };

        var root = Path.Combine(Path.GetTempPath(), "athlon-manual-compact", Guid.NewGuid().ToString("N"));
        var paths = new CompactionTests.TestAppPathProvider(root);
        paths.EnsureCreated();
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var pipeline = new PreCompletionPipeline(
            new ConversationCompactor(
                settings,
                new SummaryModelClient("manual summary"),
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger()),
            new TruncateArgsService(),
            settings,
            new NoOpLogger());
        var compactionMiddleware = new Athlon.Agent.Core.Middleware.CompactionTurnMiddleware(
            pipeline,
            new TokenEstimatorCalibrator(settings),
            new PromptPressureStore(),
            storage,
            settings);
        var orchestrator = PromptTestHelpers.CreateStaticOrchestrator();
        return new SessionCompactionService(
            compactionMiddleware,
            new EnvironmentPromptBuilderAdapter(orchestrator),
            new NoOpToolRouter(),
            orchestrator,
            settings);
    }

    private sealed class SummaryModelClient(string content) : IAgentModelClient
    {
        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentModelResponse(content, Array.Empty<AgentToolCall>()));
    }
}
