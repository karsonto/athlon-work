using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure;

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
        using var logger = AppLogger.Create(new LoggingSettings(), paths.LogsPath);
        var storage = new FileStorageService(logger, paths, new JsonFileStore());
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
                    new Dictionary<string, string>(),
                    true,
                    "listed",
                    "ok",
                    null,
                    12));
            await storage.SaveSessionAsync(session);

            var sessionDir = Path.Combine(paths.SessionsPath, session.Id);
            Assert.True(File.Exists(Path.Combine(sessionDir, "session.json")));
            Assert.True(File.Exists(Path.Combine(sessionDir, "conversation.md")));
            Assert.True(File.Exists(Path.Combine(sessionDir, "conversation.jsonl")));
            Assert.True(File.Exists(Path.Combine(sessionDir, "tool-calls", "calls.jsonl")));

            var conversationLine = await File.ReadAllTextAsync(Path.Combine(sessionDir, "conversation.jsonl"));
            Assert.Contains("你好", conversationLine);
            Assert.DoesNotContain(@"\u4f60", conversationLine);

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
    public async Task AgentRuntime_PersistsUserMessageBeforeModelReturns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-runtime-log-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        using var logger = AppLogger.Create(new LoggingSettings(), paths.LogsPath);
        var storage = new FileStorageService(logger, paths, new JsonFileStore());
        var modelClient = new CancelOnFirstCallModelClient();
        var runtime = new AgentRuntime(
            modelClient,
            storage,
            new EmptyToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            new NoOpPreCompletionPipeline(),
            new PassThroughToolResultEvictor(),
            new NoOpActiveAgentSessionContext(),
            new AppSettings(),
            new NoOpLogger());

        var session = AgentSession.Create("cancel-persist");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await runtime.SendAsync(session, "取消前也要保存", null, null, cts.Token);
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
        public Task<AgentModelResponse> CompleteAsync(AgentModelRequest request, Func<string, Task>? onTextDelta = null, Func<string, Task>? onReasoningDelta = null, Func<StreamingToolCallDelta, Task>? onToolCallDelta = null, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<AgentModelResponse>(cancellationToken);
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
