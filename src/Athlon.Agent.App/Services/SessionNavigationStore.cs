using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

/// <summary>Caches session metadata and first display pages for history navigation.</summary>
public sealed class SessionNavigationStore
{
    private readonly IFileStorageService _storage;
    private readonly int _capacity;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();

    public SessionNavigationStore(IFileStorageService storage, int capacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _storage = storage;
        _capacity = capacity;
    }

    public async Task<SessionNavigationSnapshot?> LoadSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var sessionTask = LoadSessionAsync(sessionId, cancellationToken);
        var displayTask = LoadFirstDisplayPageAsync(sessionId, cancellationToken);
        await Task.WhenAll(sessionTask, displayTask).ConfigureAwait(true);

        var session = await sessionTask.ConfigureAwait(true);
        if (session is null)
        {
            return null;
        }

        var displayPage = await displayTask.ConfigureAwait(true);
        return new SessionNavigationSnapshot(session, displayPage.Messages, displayPage.OlderCursor);
    }

    public Task<ConversationDisplayPage> LoadOlderDisplayPageAsync(
        string sessionId,
        ConversationDisplayCursor cursor,
        int pageSize = 100,
        CancellationToken cancellationToken = default) =>
        _storage.LoadConversationDisplayPageAsync(sessionId, cursor, pageSize, cancellationToken);

    private async Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(sessionId, out var cached) && cached.Session is not null)
            {
                Touch(cached);
                return cached.Session;
            }
        }

        var loaded = await _storage.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(true);
        if (loaded is not null)
        {
            lock (_cacheLock)
            {
                GetOrCreateEntry(sessionId).Session = loaded;
            }
        }

        return loaded;
    }

    private async Task<ConversationDisplayPage> LoadFirstDisplayPageAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(sessionId, out var cached) && cached.DisplayPage is not null)
            {
                Touch(cached);
                return cached.DisplayPage;
            }
        }

        var page = await _storage.LoadConversationDisplayPageAsync(
            sessionId,
            cursor: null,
            pageSize: 100,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        lock (_cacheLock)
        {
            GetOrCreateEntry(sessionId).DisplayPage = page;
        }

        return page;
    }

    public async Task<AgentSession?> SaveIfNotEmptyAsync(AgentSession session)
    {
        if (session.Messages.Count == 0)
        {
            return null;
        }

        var toSave = SessionHistoryCoordinator.DeriveSessionTitle(session);
        await _storage.SaveSessionAsync(toSave).ConfigureAwait(true);
        Invalidate(toSave.Id);
        return toSave;
    }

    public void Invalidate(string sessionId)
    {
        lock (_cacheLock)
        {
            if (_cache.Remove(sessionId, out var entry))
            {
                _lru.Remove(entry.Node);
            }
        }
    }

    private CacheEntry GetOrCreateEntry(string sessionId)
    {
        if (_cache.TryGetValue(sessionId, out var entry))
        {
            Touch(entry);
            return entry;
        }

        var node = _lru.AddFirst(sessionId);
        entry = new CacheEntry(node);
        _cache[sessionId] = entry;
        while (_cache.Count > _capacity)
        {
            var oldest = _lru.Last!;
            _lru.RemoveLast();
            _cache.Remove(oldest.Value);
        }

        return entry;
    }

    private void Touch(CacheEntry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private sealed class CacheEntry(LinkedListNode<string> node)
    {
        public LinkedListNode<string> Node { get; } = node;
        public AgentSession? Session { get; set; }
        public ConversationDisplayPage? DisplayPage { get; set; }
    }
}

public sealed record SessionNavigationSnapshot(
    AgentSession Session,
    IReadOnlyList<ChatMessage> DisplayMessages,
    ConversationDisplayCursor? OlderDisplayCursor);
