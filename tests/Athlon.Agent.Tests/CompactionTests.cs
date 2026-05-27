using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class CompactionTests
{
    [Fact]
    public void ContextCompactionSettings_AutoCompactThreshold_IsEightyPercentOfWindow()
    {
        var settings = new ContextCompactionSettings
        {
            ContextWindowTokens = 256_000,
            AutoCompactThresholdRatio = 0.80
        };

        Assert.Equal(204_800, settings.AutoCompactTokenThreshold);
        Assert.Equal(128_000, settings.MicrocompactAggressiveTokenThreshold);
    }

    [Fact]
    public void ContextTokenEstimator_UsesLengthDividedByFour()
    {
        var messages = new[]
        {
            ChatMessage.Create(MessageRole.User, new string('x', 400))
        };

        var jsonLength = JsonSerializer.Serialize(messages).Length;
        Assert.Equal(jsonLength / 4, ContextTokenEstimator.Estimate(messages));
    }

    [Fact]
    public void MicrocompactService_ClearsOlderToolMessages()
    {
        var service = new MicrocompactService();
        var messages = Enumerable.Range(0, 10)
            .Select(_ => ChatMessage.Create(MessageRole.Tool, new string('o', 200)))
            .ToList();

        service.Apply(messages, keepToolMessages: 3);

        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(MicrocompactService.ClearedContent, messages[i].Content);
        }

        for (var i = 7; i < 10; i++)
        {
            Assert.Equal(200, messages[i].Content.Length);
        }
    }

    [Fact]
    public async Task PreCompletionPipeline_BelowThreshold_OnlyMicrocompacts()
    {
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                ContextWindowTokens = 256_000,
                AutoCompactThresholdRatio = 0.80
            }
        };

        var messages = Enumerable.Range(0, 10)
            .Select(_ => ChatMessage.Create(MessageRole.Tool, new string('o', 200)))
            .ToList();
        var session = AgentSession.Create("test").WithMessages(messages);

        var autoCompact = new FakeAutoCompactService();
        var pipeline = new PreCompletionPipeline(settings, new MicrocompactService(), autoCompact, new NoOpLogger());

        var result = await pipeline.RunAsync(session);

        Assert.Equal(0, autoCompact.CallCount);
        Assert.Equal(MicrocompactService.ClearedContent, result.Messages[0].Content);
    }

    [Fact]
    public async Task PreCompletionPipeline_AboveThreshold_TriggersAutoCompact()
    {
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                ContextWindowTokens = 256_000,
                AutoCompactThresholdRatio = 0.80
            }
        };

        var largeContent = new string('x', 900_000);
        var session = AgentSession.Create("test")
            .WithMessages(new[] { ChatMessage.Create(MessageRole.User, largeContent) });

        var autoCompact = new FakeAutoCompactService();
        var pipeline = new PreCompletionPipeline(settings, new MicrocompactService(), autoCompact, new NoOpLogger());

        await pipeline.RunAsync(session);

        Assert.Equal(1, autoCompact.CallCount);
    }

    [Fact]
    public async Task AutoCompactService_ReplacesMessagesAndWritesTranscript()
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
                    SummaryMaxTokens = 512,
                    MaxConversationCharsForSummary = 10_000
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore());
            var session = AgentSession.Create("compact-session")
                .WithMessages(new[]
                {
                    ChatMessage.Create(MessageRole.User, "hello"),
                    ChatMessage.Create(MessageRole.Assistant, "hi"),
                    ChatMessage.Create(MessageRole.Tool, "tool output")
                });

            var autoCompact = new AutoCompactService(
                settings,
                new FakeModelClient("summary text"),
                storage,
                new NoOpLogger());

            var result = await autoCompact.CompactAsync(session);

            Assert.Single(result.Messages);
            Assert.Contains("Transcript:", result.Messages[0].Content, StringComparison.Ordinal);
            Assert.Contains("summary text", result.Messages[0].Content, StringComparison.Ordinal);

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

    private sealed class FakeAutoCompactService : IAutoCompactService
    {
        public int CallCount { get; private set; }

        public Task<AgentSession> CompactAsync(AgentSession session, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(session.WithMessages(new[]
            {
                ChatMessage.Create(MessageRole.User, "[Compressed. Transcript: fake]\nsummary")
            }));
        }
    }

    private sealed class FakeModelClient(string content) : IAgentModelClient
    {
        public Task<AgentModelResponse> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken = default) =>
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

    private sealed class TestAppPathProvider(string root) : IAppPathProvider
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
