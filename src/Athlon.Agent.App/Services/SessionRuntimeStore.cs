using System.Collections.Concurrent;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed class RuntimeSessionEntry
{
    public AgentSession? Session { get; set; }

    public bool Hydrated { get; set; }

    public ConversationDisplayCursor? OlderDisplayCursor { get; set; }

    internal List<ChatMessage> PendingAppends { get; } = [];

    internal HashSet<string> PendingAppendIds { get; } = new(StringComparer.Ordinal);
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
        ConversationDisplayCursor? olderDisplayCursor = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var entry = _sessions.GetOrAdd(session.Id, _ => new RuntimeSessionEntry());
        lock (_gate)
        {
            entry.Session = session;
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
            for (var index = 0; index < entry.PendingAppends.Count; index++)
            {
                if (string.Equals(entry.PendingAppends[index].Id, message.Id, StringComparison.Ordinal))
                {
                    entry.PendingAppends[index] = message;
                    break;
                }
            }
            else
            {
                entry.PendingAppendIds.Add(message.Id);
                entry.PendingAppends.Add(message);
            }
        }

        await FlushSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
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

            await _storage.ReplaceConversationDisplayAsync(session.Id, messages, cancellationToken)
                .ConfigureAwait(false);
            await _storage.SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
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
        ChatMessage[] pending;
        AgentSession? toSave;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry))
            {
                return;
            }

            pending = entry.PendingAppends.ToArray();
            entry.PendingAppends.Clear();
            entry.PendingAppendIds.Clear();
            toSave = entry.Session;
        }

        if (pending.Length == 0 && toSave is null)
        {
            return;
        }

        var appendedCount = 0;
        try
        {
            foreach (var message in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _storage.AppendConversationMessageAsync(sessionId, message, cancellationToken)
                    .ConfigureAwait(false);
                appendedCount++;
            }

            if (toSave is not null)
            {
                await _storage.SaveSessionAsync(toSave, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_gate)
            {
                if (_sessions.TryGetValue(sessionId, out var entry))
                {
                    for (var index = pending.Length - 1; index >= appendedCount; index--)
                    {
                        var message = pending[index];
                        if (entry.PendingAppendIds.Add(message.Id))
                        {
                            entry.PendingAppends.Insert(0, message);
                        }
                    }
                }
            }

            throw;
        }
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
