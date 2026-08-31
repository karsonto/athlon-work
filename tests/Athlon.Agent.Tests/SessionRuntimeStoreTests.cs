using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionRuntimeStoreTests
{
    [Fact]
    public async Task AppendAsync_writes_immediately()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage);
        var session = AgentSession.Create("live");
        var message = ChatMessage.Create(MessageRole.User, "hello");
        store.Attach(session, hydrated: true);
        store.UpdateSession(session.WithMessage(message));

        await store.AppendAsync(session.Id, message);

        Assert.Equal(message.Id, Assert.Single(storage.Appended).Message.Id);
        Assert.Empty(storage.Saved);
    }

    [Fact]
    public async Task MarkSessionDirtyAsync_writes_session_json_immediately()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage);
        var session = AgentSession.Create("meta");
        var message = ChatMessage.Create(MessageRole.User, "hello");
        var withMessage = session.WithMessage(message);
        store.Attach(withMessage, hydrated: true);
        await store.AppendAsync(session.Id, message);

        await store.MarkSessionDirtyAsync(withMessage);

        Assert.Equal(session.Id, Assert.Single(storage.Saved).Id);
    }

    [Fact]
    public async Task FlushAllAsync_writes_every_attached_session()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage);
        var first = AgentSession.Create("a");
        var second = AgentSession.Create("b");
        var m1 = ChatMessage.Create(MessageRole.User, "one");
        var m2 = ChatMessage.Create(MessageRole.User, "two");
        var firstWith = first.WithMessage(m1);
        var secondWith = second.WithMessage(m2);
        store.Attach(firstWith, hydrated: true);
        store.Attach(secondWith, hydrated: true);
        await store.AppendAsync(first.Id, m1);
        await store.AppendAsync(second.Id, m2);
        storage.Appended.Clear();
        storage.Saved.Clear();

        await store.MarkSessionDirtyAsync(firstWith);
        await store.MarkSessionDirtyAsync(secondWith);

        Assert.Equal(2, storage.Saved.Count);
    }

    [Fact]
    public async Task FlushSessionAsync_requeues_unwritten_messages_after_failure()
    {
        var storage = new RecordingStorage { RemainingAppendFailures = 1 };
        using var store = new SessionRuntimeStore(storage);
        var session = AgentSession.Create("retry");
        var message = ChatMessage.Create(MessageRole.Assistant, "keep me");
        store.Attach(session.WithMessage(message), hydrated: true);

        await Assert.ThrowsAsync<IOException>(() => store.AppendAsync(session.Id, message));
        await store.AppendAsync(session.Id, message);

        Assert.Equal(message.Id, Assert.Single(storage.Appended).Message.Id);
    }

    [Fact]
    public async Task Concurrent_flushes_for_same_session_preserve_append_order()
    {
        var storage = new RecordingStorage { AppendDelay = TimeSpan.FromMilliseconds(50) };
        using var store = new SessionRuntimeStore(storage);
        var session = AgentSession.Create("ordered");
        var first = ChatMessage.Create(MessageRole.User, "first");
        var second = ChatMessage.Create(MessageRole.Assistant, "second");
        store.Attach(session.WithMessages([first, second]), hydrated: true);

        var firstFlush = store.AppendAsync(session.Id, first);
        await Task.Delay(10);
        var secondFlush = store.AppendAsync(session.Id, second);
        await Task.WhenAll(firstFlush, secondFlush);

        Assert.Equal([first.Id, second.Id], storage.Appended.Select(item => item.Message.Id));
        Assert.Equal(1, storage.MaxConcurrentAppends);
    }

    [Fact]
    public void TryGetHydrated_is_true_after_attach_and_false_before()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage);
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
        using var store = new SessionRuntimeStore(storage);
        var stale = AgentSession.Create("switching");
        var latest = stale.WithMessage(ChatMessage.Create(MessageRole.User, "new turn"));
        store.Attach(latest, hydrated: true);

        store.UpdateSession(stale);

        Assert.True(store.TryGetHydrated(stale.Id, out var live));
        Assert.Same(latest, live.Session);
        Assert.Single(live.Session!.Messages);
    }

    [Fact]
    public async Task UpsertAsync_replaces_pending_message_with_same_id()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage);
        var session = AgentSession.Create("upsert");
        store.Attach(session, hydrated: true);

        var checkpoint = ChatMessage.CreateWithId("a1", MessageRole.Assistant, "partial");
        var final = ChatMessage.CreateWithId("a1", MessageRole.Assistant, "partial and complete");
        await store.UpsertAsync(session.Id, checkpoint);
        storage.Appended.Clear();
        await store.UpsertAsync(session.Id, final);

        var written = Assert.Single(storage.Appended);
        Assert.Equal("a1", written.Message.Id);
        Assert.Equal("partial and complete", written.Message.Content);
    }

    [Fact]
    public async Task AppendAsync_after_flush_allows_same_id_second_line()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage);
        var session = AgentSession.Create("dup");
        store.Attach(session, hydrated: true);

        var checkpoint = ChatMessage.CreateWithId("a1", MessageRole.Assistant, "partial");
        var final = ChatMessage.CreateWithId("a1", MessageRole.Assistant, "final");
        await store.UpsertAsync(session.Id, checkpoint);
        storage.Appended.Clear();
        await store.AppendAsync(session.Id, final);

        var written = Assert.Single(storage.Appended);
        Assert.Equal("final", written.Message.Content);
    }

    [Fact]
    public async Task DiscardPending_drops_queued_appends_before_next_write()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage);
        var session = AgentSession.Create("clear");
        var message = ChatMessage.Create(MessageRole.User, "gone");
        store.Attach(session, hydrated: true);

        lock (storage.Sync)
        {
            storage.BlockAppends = true;
        }

        var appendTask = store.AppendAsync(session.Id, message);
        await Task.Delay(50);
        store.DiscardPending(session.Id);
        lock (storage.Sync)
        {
            storage.BlockAppends = false;
        }

        await appendTask;
        Assert.Empty(storage.Appended);
    }

    [Fact]
    public async Task ReplaceDisplayAsync_replaces_log_and_saves_session()
    {
        var storage = new RecordingStorage();
        using var store = new SessionRuntimeStore(storage);
        var session = AgentSession.Create("replace");
        var user = ChatMessage.Create(MessageRole.User, "hi");
        var withMessage = session.WithMessage(user);
        store.Attach(withMessage, hydrated: true);

        await store.ReplaceDisplayAsync(withMessage, withMessage.Messages);

        Assert.Equal(withMessage.Messages, storage.ReplacedMessages);
        Assert.Equal(withMessage.Id, Assert.Single(storage.Saved).Id);
    }

    private sealed class RecordingStorage : IFileStorageService
    {
        public object Sync { get; } = new();
        public bool BlockAppends { get; set; }
        public List<(string SessionId, ChatMessage Message)> Appended { get; } = [];
        public List<AgentSession> Saved { get; } = [];
        public IReadOnlyList<ChatMessage> ReplacedMessages { get; private set; } = Array.Empty<ChatMessage>();
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
            while (true)
            {
                lock (Sync)
                {
                    if (!BlockAppends)
                    {
                        break;
                    }
                }

                await Task.Delay(10, cancellationToken);
            }

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

        public Task ReplaceConversationDisplayAsync(
            string sessionId,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            ReplacedMessages = messages;
            return Task.CompletedTask;
        }

        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentSession?>(null);

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
    }
}
