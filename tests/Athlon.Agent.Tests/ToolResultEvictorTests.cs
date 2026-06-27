using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ToolResultEvictorTests
{
    [Fact]
    public async Task EvictIfNeeded_WritesFileAndReturnsPlaceholder()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-evict", Guid.NewGuid().ToString("N"));
        var paths = new CompactionTests.TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var settings = new AppSettings
            {
                ContextCompaction = new ContextCompactionSettings
                {
                    ToolResultEviction = new ToolResultEvictionSettings
                    {
                        MaxResultChars = 100,
                        PreviewChars = 20
                    }
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var evictor = new ToolResultEvictor(settings, storage);
            var toolCall = new AgentToolCall("call-1", "execute_command", new Dictionary<string, string>());
            var result = ToolResult.Success("done", new string('z', 500));

            var formatted = await evictor.EvictIfNeededAsync(
                "session-1",
                toolCall,
                result,
                AgentRuntime.FormatToolResult(toolCall, result));

            Assert.Contains("[Tool result evicted", formatted, StringComparison.Ordinal);
            Assert.Contains("Archived at:", formatted, StringComparison.Ordinal);

            var evictedPath = Path.Combine(paths.SessionsPath, "session-1", "evicted", "call-1.txt");
            Assert.True(File.Exists(evictedPath));
            Assert.Equal(500, (await File.ReadAllTextAsync(evictedPath)).Length);
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
    public async Task EvictIfNeeded_SkipsExcludedTools()
    {
        var settings = new AppSettings();
        var evictor = new ToolResultEvictor(settings, new NoOpStorage());
        var toolCall = new AgentToolCall("call-2", "file_write", new Dictionary<string, string>());
        var result = ToolResult.Success("done", new string('a', 500));
        var formatted = AgentRuntime.FormatToolResult(toolCall, result);

        var output = await evictor.EvictIfNeededAsync("session-2", toolCall, result, formatted);

        Assert.Equal(formatted, output);
    }

    [Fact]
    public async Task EvictIfNeeded_EvictsLargeFileReadResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-evict-read", Guid.NewGuid().ToString("N"));
        var paths = new CompactionTests.TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var settings = new AppSettings
            {
                ContextCompaction = new ContextCompactionSettings
                {
                    ToolResultEviction = new ToolResultEvictionSettings
                    {
                        MaxResultChars = 100,
                        PreviewChars = 20
                    }
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var evictor = new ToolResultEvictor(settings, storage);
            var toolCall = new AgentToolCall("call-read", "file_read", new Dictionary<string, string>());
            var result = ToolResult.Success("read", new string('r', 500));
            var formatted = AgentRuntime.FormatToolResult(toolCall, result);

            var output = await evictor.EvictIfNeededAsync("session-read", toolCall, result, formatted);

            Assert.Contains("[Tool result evicted", output, StringComparison.Ordinal);
            Assert.Contains("Archived at:", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class NoOpStorage : IFileStorageService
    {
        public string RootPath => "/tmp";

        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentSession?>(null);

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult("/tmp/t.jsonl");

        public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) =>
            Task.FromResult("/tmp/evicted.txt");

        public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());

        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());

        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());
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
