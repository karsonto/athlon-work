using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionRuntimeStoreTests
{
    [Fact]
    public async Task AppendAsync_does_not_write_until_flush()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage, enablePeriodicFlush: false);
        var session = AgentSession.Create("live");
        var message = ChatMessage.Create(MessageRole.User, "hello");
        store.Attach(session, hydrated: true);
        store.UpdateSession(session.WithMessage(message));

        await store.AppendAsync(session.Id, message);

        Assert.Empty(storage.Appended);
        Assert.Empty(storage.Saved);

        await store.FlushSessionAsync(session.Id);

        Assert.Equal(message.Id, Assert.Single(storage.Appended).Message.Id);
        Assert.Empty(storage.Saved);
    }

    [Fact]
    public async Task FlushSessionAsync_writes_session_json_only_when_marked_dirty()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage, enablePeriodicFlush: false);
        var session = AgentSession.Create("meta");
        var message = ChatMessage.Create(MessageRole.User, "hello");
        store.Attach(session, hydrated: true);
        store.UpdateSession(session.WithMessage(message));
        await store.AppendAsync(session.Id, message);
        await store.FlushSessionAsync(session.Id);
        Assert.Empty(storage.Saved);

        await store.MarkSessionDirtyAsync(session.WithMessage(message));
        await store.FlushSessionAsync(session.Id);
        Assert.Equal(session.Id, Assert.Single(storage.Saved).Id);
    }

    [Fact]
    public async Task FlushAllAsync_writes_every_dirty_session()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage, enablePeriodicFlush: false);
        var first = AgentSession.Create("a");
        var second = AgentSession.Create("b");
        var m1 = ChatMessage.Create(MessageRole.User, "one");
        var m2 = ChatMessage.Create(MessageRole.User, "two");
        store.Attach(first.WithMessage(m1), hydrated: true);
        store.Attach(second.WithMessage(m2), hydrated: true);
        await store.AppendAsync(first.Id, m1);
        await store.AppendAsync(second.Id, m2);
        await store.MarkSessionDirtyAsync(first.WithMessage(m1));
        await store.MarkSessionDirtyAsync(second.WithMessage(m2));

        await store.FlushAllAsync();

        Assert.Equal(2, storage.Appended.Count);
        Assert.Equal(2, storage.Saved.Count);
    }

    [Fact]
    public async Task FlushSessionAsync_requeues_unwritten_messages_after_failure()
    {
        var storage = new RecordingStorage { RemainingAppendFailures = 1 };
        using var store = new SessionRuntimeStore(storage, enablePeriodicFlush: false);
        var session = AgentSession.Create("retry");
        var message = ChatMessage.Create(MessageRole.Assistant, "keep me");
        store.Attach(session.WithMessage(message), hydrated: true);
        await store.AppendAsync(session.Id, message);

        await Assert.ThrowsAsync<IOException>(() => store.FlushSessionAsync(session.Id));
        await store.FlushSessionAsync(session.Id);

        Assert.Equal(message.Id, Assert.Single(storage.Appended).Message.Id);
    }

    [Fact]
    public async Task Concurrent_flushes_for_same_session_preserve_append_order()
    {
        var storage = new RecordingStorage { AppendDelay = TimeSpan.FromMilliseconds(50) };
        using var store = new SessionRuntimeStore(storage, enablePeriodicFlush: false);
        var session = AgentSession.Create("ordered");
        var first = ChatMessage.Create(MessageRole.User, "first");
        var second = ChatMessage.Create(MessageRole.Assistant, "second");
        store.Attach(session.WithMessages([first, second]), hydrated: true);
        await store.AppendAsync(session.Id, first);

        var firstFlush = store.FlushSessionAsync(session.Id);
        await Task.Delay(10);
        await store.AppendAsync(session.Id, second);
        var secondFlush = store.FlushSessionAsync(session.Id);
        await Task.WhenAll(firstFlush, secondFlush);

        Assert.Equal([first.Id, second.Id], storage.Appended.Select(item => item.Message.Id));
        Assert.Equal(1, storage.MaxConcurrentAppends);
    }

    [Fact]
    public void TryGetHydrated_is_true_after_attach_and_false_before()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage, enablePeriodicFlush: false);
        var session = AgentSession.Create("hist");

        Assert.False(store.TryGetHydrated(session.Id, out _));
        store.Attach(session);
        Assert.False(store.TryGetHydrated(session.Id, out _));
        store.MarkHydrated(session.Id, olderDisplayCursor: null);
        Assert.True(store.TryGetHydrated(session.Id, out var live));
        Assert.Equal(session.Id, live.Session!.Id);
    }

    [Fact]
    public void UpdateSession_does_not_replace_newer_background_turn_with_stale_snapshot()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage, enablePeriodicFlush: false);
        var stale = AgentSession.Create("switching");
        var latest = stale.WithMessage(ChatMessage.Create(MessageRole.User, "new turn"));
        store.Attach(latest, hydrated: true);

        store.UpdateSession(stale);

        Assert.True(store.TryGetHydrated(stale.Id, out var live));
        Assert.Same(latest, live.Session);
        Assert.Single(live.Session!.Messages);
    }

    [Fact]
    public async Task DiscardPending_drops_queued_appends()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage, enablePeriodicFlush: false);
        var session = AgentSession.Create("clear");
        var message = ChatMessage.Create(MessageRole.User, "gone");
        store.Attach(session, hydrated: true);
        await store.AppendAsync(session.Id, message);
        store.DiscardPending(session.Id);
        await store.FlushSessionAsync(session.Id);

        Assert.Empty(storage.Appended);
    }

    private sealed class RecordingStorage : IFileStorageService
    {
        public List<(string SessionId, ChatMessage Message)> Appended { get; } = [];
        public List<AgentSession> Saved { get; } = [];
        public int RemainingAppendFailures { get; set; }
        public TimeSpan AppendDelay { get; set; }
        public int MaxConcurrentAppends { get; private set; }
        private int _concurrentAppends;

        public string RootPath => "/tmp";

        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default)
        {
            Saved.Add(session);
            return Task.CompletedTask;
        }

        public async Task AppendConversationMessageAsync(
            string sessionId,
            ChatMessage message,
            CancellationToken cancellationToken = default)
        {
            var concurrent = Interlocked.Increment(ref _concurrentAppends);
            MaxConcurrentAppends = Math.Max(MaxConcurrentAppends, concurrent);
            try
            {
                if (RemainingAppendFailures > 0)
                {
                    RemainingAppendFailures--;
                    throw new IOException("append failed");
                }

                if (AppendDelay > TimeSpan.Zero)
                {
                    await Task.Delay(AppendDelay, cancellationToken);
                }

                Appended.Add((sessionId, message));
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentAppends);
            }
        }

        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentSession?>(null);

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
        public Task ReplaceConversationDisplayAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
    }
}
