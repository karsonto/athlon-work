using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionNavigationStoreTests
{
    [Fact]
    public async Task LoadSessionAsync_UsesCachedSession()
    {
        var storage = new CapturingStorage();
        var session = AgentSession.Create("cached");
        storage.SessionToLoad = session;
        var store = new SessionNavigationStore(storage);

        var first = await store.LoadSnapshotAsync(session.Id);
        var second = await store.LoadSnapshotAsync(session.Id);

        Assert.Same(session, first!.Session);
        Assert.Same(session, second!.Session);
        Assert.Equal(1, storage.LoadSessionCount);
    }

    [Fact]
    public async Task LoadDisplayMessagesAsync_UsesCachedDisplayLog()
    {
        var storage = new CapturingStorage
        {
            DisplayMessagesToLoad =
            [
                ChatMessage.Create(MessageRole.User, "hello")
            ]
        };
        var store = new SessionNavigationStore(storage);

        storage.SessionToLoad = AgentSession.Create("session-1");

        var first = await store.LoadSnapshotAsync("session-1");
        var second = await store.LoadSnapshotAsync("session-1");

        Assert.Same(first!.DisplayMessages, second!.DisplayMessages);
        Assert.Equal(1, storage.LoadDisplayCount);
    }

    [Fact]
    public async Task SaveIfNotEmptyAsync_DerivesTitleAndInvalidatesCaches()
    {
        var storage = new CapturingStorage();
        var session = AgentSession.Create("New Chat")
            .WithMessage(ChatMessage.Create(MessageRole.User, "please summarize this session"));
        storage.SessionToLoad = session;
        storage.DisplayMessagesToLoad = session.Messages;
        var store = new SessionNavigationStore(storage);

        await store.LoadSnapshotAsync(session.Id);

        var saved = await store.SaveIfNotEmptyAsync(session);
        await store.LoadSnapshotAsync(session.Id);

        Assert.NotNull(saved);
        Assert.Equal("please summarize this session", saved!.Title);
        Assert.Equal(saved, storage.SavedSession);
        Assert.Equal(2, storage.LoadSessionCount);
        Assert.Equal(2, storage.LoadDisplayCount);
    }

    private sealed class CapturingStorage : IFileStorageService
    {
        public string RootPath => "/tmp";
        public AgentSession? SessionToLoad { get; set; }
        public IReadOnlyList<ChatMessage> DisplayMessagesToLoad { get; set; } = Array.Empty<ChatMessage>();
        public AgentSession? SavedSession { get; private set; }
        public int LoadSessionCount { get; private set; }
        public int LoadDisplayCount { get; private set; }

        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default)
        {
            SavedSession = session;
            SessionToLoad = session;
            return Task.CompletedTask;
        }

        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            LoadSessionCount++;
            return Task.FromResult(SessionToLoad);
        }

        public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            LoadDisplayCount++;
            return Task.FromResult(DisplayMessagesToLoad);
        }

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult("/tmp/t.jsonl");
        public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) => Task.FromResult("/tmp/evicted.txt");
        public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
    }
}
