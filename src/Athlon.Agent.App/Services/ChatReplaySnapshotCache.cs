using System.Collections.Generic;

namespace Athlon.Agent.App.Services;

/// <summary>
/// LRU cache of the serialized replay event shards for a session, keyed by session id and guarded
/// by a content <c>revision</c>. Building the replay stream re-renders every message's markdown to
/// HTML (<see cref="ChatEventSerializer.BuildReplayEvents"/>), so reusing the shards on a switch
/// back to a recently viewed session removes that work from the switch path.
///
/// Correctness is deliberately conservative: a lookup only hits when the stored revision equals
/// the caller-computed revision. Any change to the transcript, plan card, tool state, culture or
/// show-tool-calls flag changes the revision, so a stale shard can never be served — the worst
/// case is a miss and a rebuild.
///
/// The stored shards are the pre-sliced AG-UI event arrays (batch size
/// <see cref="ConversationDisplayLimits.WebViewReplayBatchSize"/>); the render generation is not
/// part of the cache because it changes every render.
/// </summary>
public sealed class ChatReplaySnapshotCache
{
    /// <summary>Kept in line with <see cref="SessionUiCache"/> so both caches evict together.</summary>
    public const int DefaultCapacity = 4;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly int _capacity;

    public ChatReplaySnapshotCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>
    /// Rollback switch (bound to <c>Ui.CacheReplayEvents</c>). When false every lookup misses and
    /// every store is dropped, so the replay path behaves exactly as before the cache existed.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Number of cached sessions (primarily for tests and diagnostics).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Returns the cached shards when <paramref name="revision"/> matches the stored one.
    /// </summary>
    public bool TryGet(
        string sessionId,
        string revision,
        out IReadOnlyList<IReadOnlyList<string>> batches)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(revision);

        if (!Enabled)
        {
            batches = Array.Empty<IReadOnlyList<string>>();
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var entry)
                && string.Equals(entry.Revision, revision, StringComparison.Ordinal))
            {
                TouchLocked(sessionId);
                batches = entry.Batches;
                return true;
            }
        }

        batches = Array.Empty<IReadOnlyList<string>>();
        return false;
    }

    /// <summary>Stores (or replaces) the shards for a session.</summary>
    public void Set(
        string sessionId,
        string revision,
        IReadOnlyList<IReadOnlyList<string>> batches)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(batches);

        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _entries[sessionId] = new Entry(revision, batches);
            TouchLocked(sessionId);
            EvictOverflowLocked();
        }
    }

    /// <summary>Drops a session's cached shards (explicit removal mirrors <see cref="SessionUiCache.Remove"/>).</summary>
    public void Remove(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.Remove(sessionId))
            {
                _lru.Remove(sessionId);
            }
        }
    }

    /// <summary>Drops every cached shard (e.g. a global setting or culture change).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
        }
    }

    private void TouchLocked(string sessionId)
    {
        _lru.Remove(sessionId);
        _lru.AddFirst(sessionId);
    }

    private void EvictOverflowLocked()
    {
        while (_entries.Count > _capacity)
        {
            var coldest = _lru.Last;
            if (coldest is null)
            {
                return;
            }

            _lru.RemoveLast();
            _entries.Remove(coldest.Value);
        }
    }

    private sealed record Entry(string Revision, IReadOnlyList<IReadOnlyList<string>> Batches);
}
