using System.Collections.Concurrent;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed class RuntimeSessionEntry
{
    public AgentSession? Session { get; set; }

    /// <summary>
    /// False while <see cref="Session"/> is a metadata-only shell whose messages are still
    /// loading. Persisting such a session would overwrite the on-disk history with an empty
    /// message list, so the flush path skips it until the full session is attached.
    /// </summary>
    public bool SessionComplete { get; set; } = true;

    public bool Hydrated { get; set; }

    public ConversationDisplayCursor? OlderDisplayCursor { get; set; }

    internal List<ChatMessage> PendingAppends { get; } = [];

    internal HashSet<string> PendingAppendIds { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> SuppressedAppendIds { get; } = new(StringComparer.Ordinal);

    internal string? InFlightAppendMessageId { get; set; }

    internal bool SessionDirty { get; set; }
}

/// <summary>
/// Process-wide conversation manager: each message append sync-flushes
/// conversation.jsonl and session.json through a single pipeline.
/// </summary>
public sealed class SessionRuntimeStore : IConversationTranscriptWriter, IDisposable
{
    private readonly IFileStorageService _storage;
    private readonly SessionUiCache? _uiCache;
    private readonly ConcurrentDictionary<string, RuntimeSessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionFlushLocks = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    public SessionRuntimeStore(IFileStorageService storage, SessionUiCache? uiCache = null)
    {
        _storage = storage;
        _uiCache = uiCache;
    }

    /// <summary>
    /// Returns the in-memory runtime entry when a session has been attached.
    /// Display content is always loaded from disk on switch; this no longer gates cold loads.
    /// </summary>
    public bool TryGetHydrated(string sessionId, out RuntimeSessionEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var found))
        {
            return false;
        }

        if (found.Session is null || !found.Hydrated)
        {
            return false;
        }

        entry = found;
        return true;
    }

    public bool TryGetEntry(string sessionId, out RuntimeSessionEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var found))
        {
            return false;
        }

        if (found.Session is null)
        {
            return false;
        }

        entry = found;
        return true;
    }

    public RuntimeSessionEntry Attach(
        AgentSession session,
        bool hydrated = false,
        ConversationDisplayCursor? olderDisplayCursor = null,
        bool sessionComplete = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        var entry = _sessions.GetOrAdd(session.Id, _ => new RuntimeSessionEntry());
        lock (_gate)
        {
            entry.Session = session;
            entry.SessionComplete = sessionComplete;
            if (hydrated)
            {
                entry.Hydrated = true;
            }

            if (olderDisplayCursor is not null || hydrated)
            {
                entry.OlderDisplayCursor = olderDisplayCursor;
            }
        }

        return entry;
    }

    public void MarkHydrated(
        string sessionId,
        ConversationDisplayCursor? olderDisplayCursor)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return;
        }

        lock (_gate)
        {
            entry.Hydrated = true;
            entry.OlderDisplayCursor = olderDisplayCursor;
        }
    }

    public void SetOlderDisplayCursor(string sessionId, ConversationDisplayCursor? olderDisplayCursor)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return;
        }

        lock (_gate)
        {
            entry.OlderDisplayCursor = olderDisplayCursor;
        }
    }

    /// <summary>
    /// Updates the live session without allowing a stale shell snapshot to replace a
    /// newer session produced by a background turn while the user is switching chats.
    /// Structural operations that intentionally replace history use <see cref="Attach"/>.
    /// </summary>
    public void UpdateSession(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var entry = _sessions.GetOrAdd(session.Id, _ => new RuntimeSessionEntry());
        lock (_gate)
        {
            if (entry.Session is null
                || session.UpdatedAt > entry.Session.UpdatedAt
                || session.UpdatedAt == entry.Session.UpdatedAt
                    && session.Messages.Count >= entry.Session.Messages.Count)
            {
                entry.Session = session;
            }
        }
    }

    public void Remove(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _uiCache?.Remove(sessionId);
    }

    public void DiscardPending(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return;
        }

        lock (_gate)
        {
            foreach (var pendingId in entry.PendingAppendIds)
            {
                entry.SuppressedAppendIds.Add(pendingId);
            }

            if (entry.InFlightAppendMessageId is not null)
            {
                entry.SuppressedAppendIds.Add(entry.InFlightAppendMessageId);
            }

            entry.PendingAppends.Clear();
            entry.PendingAppendIds.Clear();
        }
    }

    public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) =>
        UpsertAsync(sessionId, message, cancellationToken);

    public async Task UpsertAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(message.Id))
        {
            return;
        }

        var entry = _sessions.GetOrAdd(sessionId, _ => new RuntimeSessionEntry());
        lock (_gate)
        {
            var replaced = false;
            for (var index = 0; index < entry.PendingAppends.Count; index++)
            {
                if (string.Equals(entry.PendingAppends[index].Id, message.Id, StringComparison.Ordinal))
                {
                    entry.PendingAppends[index] = message;
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                entry.PendingAppendIds.Add(message.Id);
                entry.PendingAppends.Add(message);
            }
        }

        await FlushPendingAppendsAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSessionDirtyAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        Attach(session);
        await FlushSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceDisplayAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        // ReplaceDisplayAsync rewrites the whole display log from the session it is handed, so it
        // must never run against a metadata-only shell (that would erase the visible history).
        if (_sessions.TryGetValue(session.Id, out var existing) && !existing.SessionComplete)
        {
            return;
        }

        Attach(session);
        var sessionFlushLock = _sessionFlushLocks.GetOrAdd(session.Id, static _ => new SemaphoreSlim(1, 1));
        await sessionFlushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_sessions.TryGetValue(session.Id, out var entry))
                {
                    entry.PendingAppends.Clear();
                    entry.PendingAppendIds.Clear();
                }
            }

            // Off-thread variants: this runs from the post-first-paint adopt path, so the display
            // rewrite and the session serialization must not block the frame the user just saw.
            await _storage.ReplaceConversationDisplayOffThreadAsync(session.Id, messages, cancellationToken)
                .ConfigureAwait(false);
            await _storage.SaveSessionOffThreadAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sessionFlushLock.Release();
        }
    }

    public async Task FlushSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var entry))
            {
                entry.SessionDirty = true;
            }
        }

        await FlushPendingAppendsAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushPendingAppendsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var sessionFlushLock = _sessionFlushLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await sessionFlushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushSessionCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sessionFlushLock.Release();
        }
    }

    private async Task FlushSessionCoreAsync(string sessionId, CancellationToken cancellationToken)
    {
        AgentSession? toSave;
        bool complete;
        var sessionDirty = false;

        while (true)
        {
            ChatMessage? message;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out var entry))
                {
                    return;
                }

                if (entry.PendingAppends.Count == 0)
                {
                    toSave = entry.Session;
                    complete = entry.SessionComplete;
                    if (entry.SessionDirty)
                    {
                        entry.SessionDirty = false;
                        sessionDirty = true;
                    }

                    break;
                }

                message = entry.PendingAppends[0];
                entry.PendingAppends.RemoveAt(0);
                entry.PendingAppendIds.Remove(message.Id);
                entry.InFlightAppendMessageId = message.Id;
            }

            try
            {
                var suppress = false;
                lock (_gate)
                {
                    if (_sessions.TryGetValue(sessionId, out var entry)
                        && entry.SuppressedAppendIds.Remove(message.Id))
                    {
                        suppress = true;
                    }
                }

                if (!suppress)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _storage.AppendConversationMessageAsync(sessionId, message, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                lock (_gate)
                {
                    if (_sessions.TryGetValue(sessionId, out var entry)
                        && entry.PendingAppendIds.Add(message.Id))
                    {
                        entry.PendingAppends.Insert(0, message);
                    }
                }

                throw;
            }
            finally
            {
                lock (_gate)
                {
                    if (_sessions.TryGetValue(sessionId, out var entry))
                    {
                        entry.InFlightAppendMessageId = null;
                    }
                }
            }
        }

        // A partial session carries metadata only; writing it would wipe the on-disk history.
        // Conversation rows still flush (they are append-only), and a later full attach (or
        // MarkHydrated) restores the session write.
        if (!sessionDirty || !complete || toSave is null)
        {
            return;
        }

        await _storage.SaveSessionAsync(toSave, cancellationToken).ConfigureAwait(false);
    }

    public async Task FlushAllAsync(CancellationToken cancellationToken = default)
    {
        string[] ids;
        lock (_gate)
        {
            ids = _sessions.Keys.ToArray();
        }

        foreach (var id in ids)
        {
            await FlushSessionAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
