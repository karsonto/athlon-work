using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure;
using System.Text.Json;

namespace Athlon.Agent.Tests;

public sealed class SessionDiskLogTests
{
    [Fact]
    public async Task AppendJsonLineAsync_WritesSingleLineRecords()
    {
        var store = new JsonFileStore();
        var path = Path.Combine(Path.GetTempPath(), $"athlon-jsonl-{Guid.NewGuid():N}.jsonl");

        try
        {
            await store.AppendJsonLineAsync(path, new { hello = "世界" });
            var text = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(Environment.NewLine + " ", text);
            Assert.Contains("世界", text);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task FileStorage_WritesConversationToolAndSessionLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-disk-logs-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        var logger = new NoOpLogger();
        var storage = new FileStorageService(logger, paths, new JsonFileStore(), new AgentRunContextAccessor());
        var session = AgentSession.Create("disk-log-session");
        var user = ChatMessage.Create(MessageRole.User, "你好");
        session = session.WithMessage(user);

        try
        {
            await storage.AppendConversationMessageAsync(session.Id, user);
            await storage.AppendToolCallLogAsync(
                session.Id,
                new SessionToolCallLogEntry(
                    DateTimeOffset.UtcNow,
                    "call-1",
                    "file_list",
                    ToolCallArguments.Empty,
                    true,
                    "listed",
                    "ok",
                    null,
                    12));
            await storage.FlushPendingToolCallLogsAsync();
            var attempt = new AgentAttemptEvent(
                DateTimeOffset.UtcNow, "attempt-1", session.Id, "turn-1", AgentAttemptKind.Tool,
                ModelCallPurpose.Chat, "file_list", "schema-1", "model-1", 10, 2,
                "success", null, 12);
            await storage.AppendAttemptEventAsync(session.Id, attempt);
            await storage.SaveSessionAsync(session);

            var sessionDir = Path.Combine(paths.SessionsPath, session.Id);
            Assert.True(File.Exists(Path.Combine(sessionDir, "session.json")));
            Assert.True(File.Exists(Path.Combine(sessionDir, "conversation.jsonl")));
            Assert.True(File.Exists(Path.Combine(sessionDir, "tool-calls", "calls.jsonl")));
            Assert.True(File.Exists(Path.Combine(sessionDir, "attempts.jsonl")));
            var attempts = await storage.LoadAttemptEventsAsync(session.Id);
            Assert.Single(attempts);
            Assert.Equal("turn-1", attempts[0].TurnId);

            var conversationLine = await File.ReadAllTextAsync(Path.Combine(sessionDir, "conversation.jsonl"));
            Assert.Contains("你好", conversationLine);
            Assert.DoesNotContain(@"\u4f60", conversationLine);

            var assistant = ChatMessage.Create(
                MessageRole.Assistant,
                "reply",
                reasoningContent: "thinking",
                toolCalls: [new AgentToolCall("call-2", "file_read", new Dictionary<string, string>())]);
            await storage.AppendConversationMessageAsync(session.Id, assistant);

            var loaded = await storage.LoadConversationDisplayAsync(session.Id);
            Assert.Equal(2, loaded.Count);
            var loadedAssistant = loaded.Single(message => message.Role == MessageRole.Assistant);
            Assert.Equal("reply", loadedAssistant.Content);
            Assert.Equal("thinking", loadedAssistant.ReasoningContent);
            Assert.Contains("call-2", loadedAssistant.ToolCallsJson, StringComparison.Ordinal);

            var toolLine = await File.ReadAllTextAsync(Path.Combine(sessionDir, "tool-calls", "calls.jsonl"));
            Assert.Contains("file_list", toolLine, StringComparison.Ordinal);
            Assert.Contains("call-1", toolLine, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReplaceConversationDisplayAsync_RewritesDisplayLog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-replace-display-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var session = AgentSession.Create("replace-display-session");
        var first = ChatMessage.Create(MessageRole.User, "first");
        var second = ChatMessage.Create(MessageRole.Assistant, "second");

        try
        {
            await storage.AppendConversationMessageAsync(session.Id, first);
            await storage.AppendConversationMessageAsync(session.Id, second);

            var replacement = new[]
            {
                CompactionMessageContent.CreateCompactionMessage(
                    CompactionMessageContent.CreateConversationCompact(100, 50, 2, null, "summary")),
                ChatMessage.Create(MessageRole.Assistant, "second")
            };
            await storage.ReplaceConversationDisplayAsync(session.Id, replacement);

            var loaded = await storage.LoadConversationDisplayAsync(session.Id);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(MessageRole.Compaction, loaded[0].Role);
            Assert.Equal(MessageRole.Assistant, loaded[1].Role);
            Assert.DoesNotContain(loaded, message => message.Content.Contains("first", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConversationDisplayPages_ReadTailThenAllEarlierMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-pages-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var sessionId = "paged-session";
        var sessionDir = Path.Combine(paths.SessionsPath, sessionId);
        Directory.CreateDirectory(sessionDir);
        var logPath = Path.Combine(sessionDir, "conversation.jsonl");
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var messages = Enumerable.Range(0, 1000)
            .Select(index => new ChatMessage(
                $"m-{index:D4}",
                MessageRole.User,
                $"第 {index} 条消息 🌏",
                start.AddSeconds(index),
                null,
                null,
                null,
                null))
            .ToArray();
        var lines = messages.Select(message => JsonSerializer.Serialize(message, JsonFileStore.JsonLineOptions));
        await File.WriteAllTextAsync(logPath, string.Join("\r\n", lines));

        try
        {
            var first = await storage.LoadConversationDisplayPageAsync(sessionId, pageSize: 100);
            Assert.Equal("m-0900", first.Messages[0].Id);
            Assert.Equal("m-0999", first.Messages[^1].Id);
            Assert.NotNull(first.OlderCursor);

            var loaded = new List<ChatMessage>(first.Messages);
            var cursor = first.OlderCursor;
            while (cursor is not null)
            {
                var page = await storage.LoadConversationDisplayPageAsync(sessionId, cursor, pageSize: 73);
                loaded.InsertRange(0, page.Messages);
                cursor = page.OlderCursor;
            }

            Assert.Equal(1000, loaded.Count);
            Assert.Equal(1000, loaded.Select(message => message.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(messages.Select(message => message.Id), loaded.Select(message => message.Id));
            Assert.Contains("🌏", loaded[123].Content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConversationDisplayPage_PreservesLegacyParsingAndDeduplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-legacy-pages-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var sessionId = "legacy-page";
        var sessionDir = Path.Combine(paths.SessionsPath, sessionId);
        Directory.CreateDirectory(sessionDir);
        var logPath = Path.Combine(sessionDir, "conversation.jsonl");
        var legacy = """{"id":"legacy-1","role":"user","content":"旧格式","time":"2026-01-01T00:00:00Z"}""";
        await File.WriteAllTextAsync(logPath, $"{legacy}\n{legacy}\n");

        try
        {
            var page = await storage.LoadConversationDisplayPageAsync(sessionId, pageSize: 10);

            var message = Assert.Single(page.Messages);
            Assert.Equal("legacy-1", message.Id);
            Assert.Equal("旧格式", message.Content);
            Assert.Null(page.OlderCursor);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConversationDisplayPage_ObservesCancellation()
    {
        var storage = new FileStorageService(
            new NoOpLogger(),
            new TestAppPathProvider(Path.GetTempPath()),
            new JsonFileStore(),
            new AgentRunContextAccessor());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.LoadConversationDisplayPageAsync(
                $"missing-{Guid.NewGuid():N}",
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task AgentRuntime_PersistsUserMessageBeforeModelReturns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-runtime-log-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        var logger = new NoOpLogger();
        var storage = new FileStorageService(logger, paths, new JsonFileStore(), new AgentRunContextAccessor());
        var modelClient = new CancelOnFirstCallModelClient();
        var settings = new AppSettings();
        var runtimeLogger = new NoOpLogger();
        var (turnPipeline, compaction) = AgentRuntimeTestFactory.CreateMiddleware(
            new NoOpPreCompletionPipeline(),
            storage,
            new TokenEstimatorCalibrator(settings),
            settings,
            runtimeLogger);
        var runtime = new AgentRuntime(
            modelClient,
            storage,
            new EmptyToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            new NoOpPreCompletionPipeline(),
            new PassThroughToolResultEvictor(),
            new TokenEstimatorCalibrator(settings),
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            new SessionToolStormStore(),
            new NoOpActiveAgentSessionContext(),
            new AgentRunContextAccessor(),
            turnPipeline,
            compaction,
            settings,
            runtimeLogger,
            new NoOpPostTurnMemoryProcessor());

        var session = AgentSession.Create("cancel-persist");

        try
        {
            await runtime.SendAsync(session, "取消前也要保存", null, null, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            var loaded = await storage.LoadSessionAsync(session.Id);
            Assert.NotNull(loaded);
            Assert.Contains(loaded!.Messages, message => message.Role == MessageRole.User && message.Content == "取消前也要保存");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class CancelOnFirstCallModelClient : IAgentModelClient
    {
        private static readonly CancellationToken Canceled = new(canceled: true);

        public Task<AgentModelResponse> CompleteAsync(AgentModelRequest request, Func<string, Task>? onTextDelta = null, Func<string, Task>? onReasoningDelta = null, Func<StreamingToolCallDelta, Task>? onToolCallDelta = null, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<AgentModelResponse>(Canceled);
    }

    private sealed class EmptyToolRouter : IToolRouter
    {
        public IReadOnlyList<ToolDefinition> ListTools() => Array.Empty<ToolDefinition>();

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StaticPromptBuilder : IAgentEnvironmentPromptBuilder
    {
        public string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools) => "prompt";
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

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
        public void Dispose() { }
    }

    private sealed class TestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(root, "config");
        public string SessionsPath => Path.Combine(root, "sessions");
        public string AuditPath => Path.Combine(root, "audit");
        public string LogsPath => Path.Combine(root, "logs");
        public string CredentialsPath => Path.Combine(root, "credentials");
        public string SkillsPath => Path.Combine(root, "skills");

        public void EnsureCreated() => Directory.CreateDirectory(root);

        public string ResolveSkillPath(string path) =>
            string.IsNullOrWhiteSpace(path) ? path : Path.Combine(SkillsPath, path);
    }
}
