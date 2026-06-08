using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.ComposerCommands;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.ComposerCommands;

namespace Athlon.Agent.Tests;

public sealed class ComposerCommandTests
{
    [Theory]
    [InlineData("/compact", "compact", 0)]
    [InlineData("/help", "help", 0)]
    [InlineData("/compact force", "compact", 1)]
    [InlineData("  /Compact  ", "Compact", 0)]
    public void Parser_parses_slash_commands(string input, string expectedCommand, int expectedArgCount)
    {
        Assert.True(ComposerCommandParser.TryParse(input, out var command, out var args));
        Assert.Equal(expectedCommand, command, ignoreCase: true);
        Assert.Equal(expectedArgCount, args.Length);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/")]
    [InlineData("text /compact")]
    public void Parser_rejects_non_commands(string input)
    {
        Assert.False(ComposerCommandParser.TryParse(input, out _, out _));
    }

    [Fact]
    public void HelpComposerCommand_lists_registered_commands()
    {
        var compact = new CompactComposerCommand(new NoOpCompactionService());
        var registry = new ComposerCommandRegistry(new IComposerCommand[] { compact });
        var help = new HelpComposerCommand(registry);
        var result = help.ExecuteAsync(new ComposerCommandContext
        {
            Session = AgentSession.Create("s"),
            Args = Array.Empty<string>(),
            Settings = new AppSettings()
        }).GetAwaiter().GetResult();

        Assert.Equal(ComposerCommandOutcome.Handled, result.Outcome);
        Assert.Contains("/compact", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Executor_rejects_unknown_command()
    {
        var compact = new CompactComposerCommand(new NoOpCompactionService());
        var executor = new ComposerCommandExecutor(new ComposerCommandRegistry(new IComposerCommand[] { compact }));
        var session = AgentSession.Create("s");
        var result = await executor.ExecuteAsync(
            "unknown",
            Array.Empty<string>(),
            session,
            new AppSettings(),
            isSessionBusy: false);

        Assert.Equal(ComposerCommandOutcome.Rejected, result.Outcome);
        Assert.Contains("未知命令", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCompactionService_manual_compact_emits_manual_audit()
    {
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                TriggerMessages = 2,
                KeepMessages = 1,
                DynamicCompaction = new DynamicCompactionSettings { Enabled = false }
            }
        };

        var session = AgentSession.Create("compact-test")
            .WithMessages(new[]
            {
                ChatMessage.Create(MessageRole.User, "one"),
                ChatMessage.Create(MessageRole.Assistant, "two"),
                ChatMessage.Create(MessageRole.User, "three")
            });

        var storage = new NoOpStorage();
        var compactor = new ManualCompactionCompactor(settings);
        var pipeline = new PreCompletionPipeline(
            compactor,
            new TruncateArgsService(),
            settings,
            new NoOpLogger());
        var service = new SessionCompactionService(
            pipeline,
            new ToolRouter(Array.Empty<IAgentTool>()),
            PromptTestHelpers.CreateStaticOrchestrator(),
            new TokenEstimatorCalibrator(),
            storage,
            settings);

        var result = await service.CompactManuallyAsync(session);

        Assert.True(result.Compacted);
        Assert.Contains(
            result.Session.Messages,
            message => message.Role == MessageRole.Compaction
                       && message.Content.Contains("manualcompact", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class NoOpCompactionService : ISessionCompactionService
    {
        public Task<SessionCompactionResult> CompactManuallyAsync(
            AgentSession session,
            AgentTurnCallbacks? callbacks = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionCompactionResult(session, false, "noop"));
    }

    private sealed class ManualCompactionCompactor(AppSettings settings) : IConversationCompactor
    {
        public Task<ConversationCompactResult> CompactIfNeededAsync(
            AgentSession session,
            CompactionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!request.Force)
            {
                return Task.FromResult(new ConversationCompactResult(session, false));
            }

            var audit = CompactionMessageContent.CreateCompactionMessage(
                CompactionMessageContent.CreateManualCompact(100, 50, 3, "fake.jsonl", "summary"));
            return Task.FromResult(new ConversationCompactResult(
                session.WithMessages(new[]
                {
                    audit,
                    SummaryMessageBuilder.CreateSummaryPlaceholder("summary", null),
                    ChatMessage.Create(MessageRole.User, "tail")
                }),
                true));
        }
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
