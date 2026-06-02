using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class CompactionTests
{
    [Fact]
    public void ContextCompactionSettings_UsesAgentScopeDefaults()
    {
        var settings = new ContextCompactionSettings();

        Assert.Equal(50, settings.TriggerMessages);
        Assert.Equal(80_000, settings.TriggerTokens);
        Assert.Equal(20, settings.KeepMessages);
        Assert.Equal(2_000, settings.TruncateArgs.MaxArgLength);
        Assert.Equal(80_000, settings.ToolResultEviction.MaxResultChars);
    }

    [Fact]
    public void ContextTokenEstimator_UsesCharsPerTokenHeuristic()
    {
        var message = ChatMessage.Create(MessageRole.User, new string('x', 250));
        var textTokens = (int)Math.Ceiling(250 / 2.5);

        Assert.True(ContextTokenEstimator.EstimateMessage(message) >= textTokens);
        Assert.Equal(ContextTokenEstimator.EstimateMessage(message), ContextTokenEstimator.Estimate(new[] { message }));
    }

    [Fact]
    public void ConversationCutoffPlanner_LongAgentLoop_CompactsWithoutSecondUserMessage()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "do the task")
        };

        for (var i = 0; i < 4; i++)
        {
            messages.Add(ChatMessage.Create(
                MessageRole.Assistant,
                $"step-{i}",
                toolCalls: new[] { new AgentToolCall($"c{i}", "file_read", new Dictionary<string, string>()) }));
            messages.Add(ChatMessage.Create(MessageRole.Tool, $"output-{i}"));
        }

        var settings = new ContextCompactionSettings { TriggerMessages = 5, KeepMessages = 2 };
        var estimated = ContextTokenEstimator.Estimate(messages);
        Assert.True(ConversationCutoffPlanner.ShouldCompact(messages, estimated, settings, force: false));

        var cutoff = ConversationCutoffPlanner.DetermineCutoffIndex(messages, estimated, settings);
        Assert.True(cutoff > 0);
        Assert.Equal(2, messages.Count - cutoff);
    }

    [Fact]
    public void ConversationCutoffPlanner_KeepTailByMessages()
    {
        var messages = new[]
        {
            ChatMessage.Create(MessageRole.User, "first"),
            ChatMessage.Create(MessageRole.Assistant, "reply-1"),
            ChatMessage.Create(MessageRole.User, "latest question"),
            ChatMessage.Create(MessageRole.Assistant, "thinking", toolCalls: new[] { new AgentToolCall("c1", "file_read", new Dictionary<string, string>()) }),
            ChatMessage.Create(MessageRole.Tool, "tool output"),
        };

        var cutoff = ConversationCutoffPlanner.DetermineCutoffIndex(
            messages,
            ContextTokenEstimator.Estimate(messages),
            new ContextCompactionSettings { KeepMessages = 2 });

        var tail = messages.Skip(cutoff).Select(message => message.Content).ToArray();
        Assert.Contains("latest question", tail);
        Assert.Contains("tool output", tail);
        Assert.DoesNotContain("first", tail);
    }

    [Fact]
    public void ConversationCutoffPlanner_FindSafeCutoff_DoesNotSplitToolPair()
    {
        var assistant = ChatMessage.Create(
            MessageRole.Assistant,
            string.Empty,
            toolCalls: new[] { new AgentToolCall("call-1", "file_read", new Dictionary<string, string>()) });
        var tool = ChatMessage.Create(MessageRole.Tool, "ToolCallId: call-1\noutput");
        var messages = new[] { assistant, tool };

        var cutoff = ConversationCutoffPlanner.FindSafeCutoffPoint(messages, 1);
        Assert.Equal(0, cutoff);
    }

    [Fact]
    public void SummaryMessageBuilder_FiltersOldSummaryMessages()
    {
        var summary = SummaryMessageBuilder.CreateSummaryPlaceholder("old summary", null);
        var user = ChatMessage.Create(MessageRole.User, "hello");
        var filtered = SummaryMessageBuilder.FilterSummaryMessages(new[] { summary, user });

        Assert.Single(filtered);
        Assert.Equal("hello", filtered[0].Content);
    }

    [Fact]
    public void SummaryMessageBuilder_WithTranscript_UsesAgentScopeFormat()
    {
        var summary = SummaryMessageBuilder.CreateSummaryPlaceholder("facts", "/tmp/transcript.jsonl");
        Assert.Contains("conversation that has been summarized", summary.Content, StringComparison.Ordinal);
        Assert.Contains("/tmp/transcript.jsonl", summary.Content, StringComparison.Ordinal);
        Assert.Contains("<summary>", summary.Content, StringComparison.Ordinal);
        Assert.True(SummaryMessageBuilder.IsSummaryMessage(summary));
    }

    [Fact]
    public async Task PreCompletionPipeline_BelowThreshold_DoesNotCompact()
    {
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                TriggerMessages = 100,
                TriggerTokens = 1_000_000
            }
        };

        var session = AgentSession.Create("test")
            .WithMessages(new[] { ChatMessage.Create(MessageRole.User, "hello") });

        var compactor = new FakeConversationCompactor(settings);
        var pipeline = new PreCompletionPipeline(compactor, new NoOpLogger());

        var result = await pipeline.RunAsync(session, PreCompletionOptions.AgentLoop);

        Assert.Equal(0, compactor.CallCount);
        Assert.Single(result.Messages);
    }

    [Fact]
    public async Task PreCompletionPipeline_AboveThreshold_TriggersConversationCompact()
    {
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                TriggerMessages = 2,
                KeepMessages = 1
            }
        };

        var session = AgentSession.Create("test")
            .WithMessages(new[]
            {
                ChatMessage.Create(MessageRole.User, "one"),
                ChatMessage.Create(MessageRole.Assistant, "two"),
                ChatMessage.Create(MessageRole.User, "three")
            });

        var compactor = new FakeConversationCompactor(settings);
        var pipeline = new PreCompletionPipeline(compactor, new NoOpLogger());

        await pipeline.RunAsync(session, PreCompletionOptions.AgentLoop);

        Assert.Equal(1, compactor.CallCount);
    }

    [Fact]
    public async Task ConversationCompactor_ReplacesPrefixWithSummaryAndTail()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-compact-tests", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var settings = new AppSettings
            {
                ContextCompaction = new ContextCompactionSettings
                {
                    TriggerMessages = 2,
                    KeepMessages = 1,
                    SummaryMaxTokens = 512,
                    MaxConversationCharsForSummary = 10_000
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore());
            var session = AgentSession.Create("compact-session")
                .WithMessages(new[]
                {
                    ChatMessage.Create(MessageRole.User, "old"),
                    ChatMessage.Create(MessageRole.Assistant, "earlier"),
                    ChatMessage.Create(MessageRole.User, "hello"),
                    ChatMessage.Create(MessageRole.Assistant, "hi"),
                    ChatMessage.Create(MessageRole.Tool, "ToolCallId: t1\ntool output")
                });

            var compactor = new ConversationCompactor(
                settings,
                new FakeModelClient("summary text"),
                storage,
                new TruncateArgsService(),
                new NoOpLogger());

            var result = await compactor.CompactIfNeededAsync(
                session,
                CompactionKind.ConversationCompact,
                force: false,
                emitAudit: true);

            Assert.True(result.Compacted);
            Assert.Equal(5, result.Session.Messages.Count);
            Assert.Equal(MessageRole.Compaction, result.Session.Messages[0].Role);
            Assert.Contains("conversationcompact", result.Session.Messages[0].Content, StringComparison.OrdinalIgnoreCase);
            Assert.True(SummaryMessageBuilder.IsSummaryMessage(result.Session.Messages[1]));
            Assert.Equal(MessageRole.Tool, result.Session.Messages[^1].Role);
            Assert.Contains(result.Session.Messages.Skip(2), message => message.Content == "hello");

            var transcriptDir = Path.Combine(paths.SessionsPath, session.Id, "transcripts");
            Assert.True(Directory.Exists(transcriptDir));
            Assert.NotEmpty(Directory.GetFiles(transcriptDir, "transcript_*.jsonl"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void CompactionMessageContent_IsSummaryPlaceholder_DetectsMarker()
    {
        var content = $"{ConversationCompactionDefaults.SummaryMessageMarker}\nsummary";
        Assert.True(CompactionMessageContent.IsSummaryPlaceholder(content));
    }

    [Fact]
    public async Task FileStorageService_RoundTripsCompactionMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-compact-rt", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore());
            var audit = CompactionMessageContent.CreateCompactionMessage(
                CompactionMessageContent.CreateConversationCompact(100, 80, 3, "fake.jsonl", "summary"));
            var session = AgentSession.Create("rt").WithMessage(audit);
            await storage.SaveSessionAsync(session);

            var loaded = await storage.LoadSessionAsync(session.Id);
            Assert.NotNull(loaded);
            Assert.Contains(loaded.Messages, message => message.Role == MessageRole.Compaction);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class FakeConversationCompactor(AppSettings settings) : IConversationCompactor
    {
        public int CallCount { get; private set; }

        public Task<ConversationCompactResult> CompactIfNeededAsync(
            AgentSession session,
            CompactionKind kind,
            bool force,
            bool emitAudit,
            CancellationToken cancellationToken = default)
        {
            var conversation = session.Messages.Where(message => message.Role != MessageRole.Compaction).ToList();
            var cfg = settings.ContextCompaction;

            if (!ConversationCutoffPlanner.ShouldCompact(
                    conversation,
                    ContextTokenEstimator.Estimate(conversation),
                    cfg,
                    force))
            {
                return Task.FromResult(new ConversationCompactResult(session, false));
            }

            CallCount++;
            var audit = CompactionMessageContent.CreateCompactionMessage(
                CompactionMessageContent.CreateConversationCompact(1, 1, conversation.Count, "fake.jsonl", "summary"));
            return Task.FromResult(new ConversationCompactResult(
                session.WithMessages(new[]
                {
                    audit,
                    SummaryMessageBuilder.CreateSummaryPlaceholder("summary", null)
                }),
                true));
        }
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

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }

    internal sealed class TestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, AppPathProvider.SkillsFolderName);

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(ConfigPath);
            Directory.CreateDirectory(SessionsPath);
            Directory.CreateDirectory(AuditPath);
            Directory.CreateDirectory(LogsPath);
            Directory.CreateDirectory(CredentialsPath);
            Directory.CreateDirectory(SkillsPath);
        }

        public string ResolveSkillPath(string path) =>
            string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
                ? path
                : Path.Combine(SkillsPath, path);
    }
}
