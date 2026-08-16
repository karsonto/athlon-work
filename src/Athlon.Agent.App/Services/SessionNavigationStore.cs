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
        var activitySource = await ExpandActivitySourceToTurnStartAsync(
                sessionId,
                displayPage.Messages,
                displayPage.OlderCursor,
                cancellationToken)
            .ConfigureAwait(true);
        return new SessionNavigationSnapshot(
            session,
            displayPage.Messages,
            displayPage.OlderCursor,
            activitySource);
    }

    /// <summary>
    /// Display pages are capped at <see cref="ConversationDisplayLimits.PageSize"/> and may start
    /// mid-turn. Activity replay needs the full turn (from user/compaction) so explored/edited
    /// counts stay stable across session switches.
    /// </summary>
    private async Task<IReadOnlyList<ChatMessage>> ExpandActivitySourceToTurnStartAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> displayMessages,
        ConversationDisplayCursor? olderCursor,
        CancellationToken cancellationToken)
    {
        if (!ConversationActivitySource.NeedsTurnStartBackfill(displayMessages)
            || olderCursor is null)
        {
            return displayMessages;
        }

        var activity = displayMessages.ToList();
        var cursor = olderCursor;
        for (var page = 0;
             page < ConversationActivitySource.MaxBackfillPages
             && ConversationActivitySource.NeedsTurnStartBackfill(activity)
             && cursor is not null;
             page++)
        {
            var older = await _storage.LoadConversationDisplayPageAsync(
                    sessionId,
                    cursor,
                    ConversationDisplayLimits.PageSize,
                    cancellationToken)
                .ConfigureAwait(true);
            if (older.Messages.Count == 0)
            {
                break;
            }

            activity = ConversationActivitySource.PrependOlder(older.Messages, activity);
            cursor = older.OlderCursor;
        }

        return activity;
    }

    public Task<ConversationDisplayPage> LoadOlderDisplayPageAsync(
        string sessionId,
        ConversationDisplayCursor cursor,
        int pageSize = ConversationDisplayLimits.PageSize,
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
            pageSize: ConversationDisplayLimits.PageSize,
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
    ConversationDisplayCursor? OlderDisplayCursor,
    IReadOnlyList<ChatMessage>? ActivitySourceMessages = null)
{
    /// <summary>
    /// Messages used to rebuild TURN_ACTIVITY / FILES_CHANGED. May be longer than
    /// <see cref="DisplayMessages"/> when the display page starts mid-turn.
    /// </summary>
    public IReadOnlyList<ChatMessage> ActivitySource =>
        ActivitySourceMessages is { Count: > 0 } ? ActivitySourceMessages : DisplayMessages;
}
