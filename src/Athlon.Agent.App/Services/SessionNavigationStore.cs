using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

/// <summary>Caches session metadata and first display pages for history navigation.</summary>
public sealed class SessionNavigationStore
{
    private readonly IFileStorageService _storage;
    private readonly IConversationTranscriptWriter? _transcriptWriter;
    private readonly int _capacity;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();

    public SessionNavigationStore(
        IFileStorageService storage,
        IConversationTranscriptWriter? transcriptWriter = null,
        int capacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _storage = storage;
        _transcriptWriter = transcriptWriter;
        _capacity = capacity;
    }

    /// <summary>
    /// Two-phase load. The returned snapshot always carries displayable content; its
    /// <see cref="SessionNavigationSnapshot.Session"/> is the full session when it is already
    /// cached or when the session has no fast metadata path, otherwise it is a metadata-only
    /// shell (<see cref="SessionNavigationSnapshot.SessionIsPartial"/>) while the full session
    /// loads in the background via <see cref="LoadFullSessionAsync"/>.
    /// </summary>
    public async Task<SessionNavigationSnapshot?> LoadSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        AgentSession session;
        bool isPartial;
        ConversationDisplayPage displayPage;

        if (TryGetCachedSession(sessionId, out var cached))
        {
            // Cache hit: the full session is already in memory, skip the metadata probe entirely.
            session = cached;
            isPartial = false;
            displayPage = await LoadFirstDisplayPageAsync(sessionId, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            var metadataTask = LoadSessionIndexEntryAsync(sessionId, cancellationToken);
            var displayTask = LoadFirstDisplayPageAsync(sessionId, cancellationToken);

            // Metadata is a scalar-only read (cheap); resolving it first lets us avoid the full
            // session.json read entirely while the display page loads concurrently.
            var metadata = await metadataTask.ConfigureAwait(true);

            if (metadata is not null)
            {
                session = CreateMetadataOnlySession(metadata);
                isPartial = true;
                // Kick off the full load now so it overlaps the caller's first paint. The snapshot
                // does not await it; the shell awaits LoadFullSessionAsync once the timeline is up.
                // Starting the load first also materializes the cache entry we flag below.
                ObserveBackgroundLoad(LoadFullSessionAsync(sessionId, cancellationToken));
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(sessionId, out var entry))
                    {
                        entry.ExistsOnDisk = true;
                    }
                }

                displayPage = await displayTask.ConfigureAwait(true);
            }
            else
            {
                // No fast metadata path (index lookup / relocated directory): fall back to a full
                // load so correctness wins over latency for these rare layouts. Run it alongside
                // the already-started display read.
                var fullTask = LoadFullSessionAsync(sessionId, cancellationToken);
                await Task.WhenAll(fullTask, displayTask).ConfigureAwait(true);
                var full = await fullTask.ConfigureAwait(true);
                if (full is null)
                {
                    return null;
                }

                session = full;
                isPartial = false;
                displayPage = await displayTask.ConfigureAwait(true);
            }
        }

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
            activitySource)
        {
            SessionIsPartial = isPartial
        };
    }

    private Task<SessionIndexEntry?> LoadSessionIndexEntryAsync(string sessionId, CancellationToken cancellationToken) =>
        _storage.LoadSessionIndexEntryAsync(sessionId, cancellationToken);

    /// <summary>
    /// The background kick-off is intentionally not awaited by <see cref="LoadSnapshotAsync"/>;
    /// the shell awaits it later through <see cref="LoadFullSessionAsync"/> and handles failures.
    /// Observe it here so a rejected load never surfaces as an unobserved task exception.
    /// </summary>
    private static void ObserveBackgroundLoad(Task<AgentSession?> task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static AgentSession CreateMetadataOnlySession(SessionIndexEntry entry) =>
        new(
            entry.Id,
            entry.Title,
            entry.UpdatedAt,
            entry.UpdatedAt,
            entry.ActiveWorkspace,
            ActiveSkill: null,
            ModelName: null,
            Messages: Array.Empty<ChatMessage>())
        {
            ActiveWorkspaceId = entry.ActiveWorkspaceId
        };

    private bool TryGetCachedSession(string sessionId, out AgentSession session)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(sessionId, out var cached) && cached.Session is not null)
            {
                Touch(cached);
                session = cached.Session;
                return true;
            }
        }

        session = null!;
        return false;
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

    /// <summary>
    /// Loads the full session (with messages), reusing an in-flight load so a switch that already
    /// started loading does not deserialize the same <c>session.json</c> twice.
    /// </summary>
    public Task<AgentSession?> LoadFullSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        Task<AgentSession?> loadTask;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(sessionId, out var cached))
            {
                if (cached.Session is not null)
                {
                    Touch(cached);
                    return Task.FromResult<AgentSession?>(cached.Session);
                }

                if (cached.FullSessionTask is not null)
                {
                    Touch(cached);
                    return cached.FullSessionTask;
                }

                loadTask = StartFullSessionLoad(cached, sessionId, cancellationToken);
                return loadTask;
            }

            var entry = GetOrCreateEntry(sessionId);
            loadTask = StartFullSessionLoad(entry, sessionId, cancellationToken);
            return loadTask;
        }
    }

    private Task<AgentSession?> StartFullSessionLoad(
        CacheEntry entry,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var task = LoadFullSessionCoreAsync(sessionId, entry, cancellationToken);
        entry.FullSessionTask = task;
        return task;
    }

    private async Task<AgentSession?> LoadFullSessionCoreAsync(
        string sessionId,
        CacheEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await _storage.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            lock (_cacheLock)
            {
                if (loaded is not null && _cache.TryGetValue(sessionId, out var current)
                    && ReferenceEquals(current, entry))
                {
                    current.Session = loaded;
                    current.ExistsOnDisk = true;
                }
            }

            return loaded;
        }
        finally
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(sessionId, out var current) && ReferenceEquals(current, entry))
                {
                    current.FullSessionTask = null;
                }
            }
        }
    }

    /// <summary>
    /// Refreshes the cached session from live in-memory state without dropping the display page,
    /// so switching back to a previously viewed session skips the full reload.
    /// </summary>
    public void UpdateCachedSession(AgentSession session)
    {
        lock (_cacheLock)
        {
            var entry = GetOrCreateEntry(session.Id);
            entry.Session = session;
        }
    }

    /// <summary>
    /// Drops only the cached first display page (the conversation log moved on) while keeping the
    /// cached session, so the next switch re-reads the tail but reuses the session payload.
    /// </summary>
    public void InvalidateDisplayPage(string sessionId)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(sessionId, out var entry))
            {
                entry.DisplayPage = null;
            }
        }
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

    /// <summary>
    /// Empty sessions that were never saved (for example the startup shell) should not be
    /// flushed into the history index when the user switches away.
    /// </summary>
    public async Task<bool> ShouldPersistOnSwitchAsync(
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        if (session.Messages.Count > 0)
        {
            return true;
        }

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(session.Id, out var cached) && cached.ExistsOnDisk)
            {
                return true;
            }
        }

        return await _storage.LoadSessionAsync(session.Id, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<AgentSession?> SaveIfNotEmptyAsync(AgentSession session)
    {
        if (session.Messages.Count == 0)
        {
            return null;
        }

        var toSave = SessionHistoryCoordinator.DeriveSessionTitle(session);
        if (_transcriptWriter is not null)
        {
            await _transcriptWriter.MarkSessionDirtyAsync(toSave).ConfigureAwait(true);
        }
        else
        {
            await _storage.SaveSessionAsync(toSave).ConfigureAwait(true);
        }

        // Keep the session object so a switch back skips the full reload; only the display page is
        // stale (the transcript changed).
        UpdateCachedSession(toSave);
        InvalidateDisplayPage(toSave.Id);
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
        /// <summary>In-flight full-session load; shared so concurrent switches deserialize once.</summary>
        public Task<AgentSession?>? FullSessionTask { get; set; }
        /// <summary>True once we know a session.json exists for this id (metadata read or full load).</summary>
        public bool ExistsOnDisk { get; set; }
    }
}

public sealed record SessionNavigationSnapshot(
    AgentSession Session,
    IReadOnlyList<ChatMessage> DisplayMessages,
    ConversationDisplayCursor? OlderDisplayCursor,
    IReadOnlyList<ChatMessage>? ActivitySourceMessages = null)
{
    /// <summary>
    /// True when <see cref="Session"/> carries metadata only and its messages still need to be
    /// loaded via <see cref="SessionNavigationStore.LoadFullSessionAsync"/>.
    /// </summary>
    public bool SessionIsPartial { get; init; }

    /// <summary>
    /// Messages used to rebuild TURN_ACTIVITY / FILES_CHANGED. May be longer than
    /// <see cref="DisplayMessages"/> when the display page starts mid-turn.
    /// </summary>
    public IReadOnlyList<ChatMessage> ActivitySource =>
        ActivitySourceMessages is { Count: > 0 } ? ActivitySourceMessages : DisplayMessages;
}
