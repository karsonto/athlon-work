using System.Collections.Concurrent;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed class RuntimeSessionEntry
{
    public AgentSession? Session { get; set; }

    public bool Hydrated { get; set; }

    public ConversationDisplayCursor? OlderDisplayCursor { get; set; }

    public bool SessionJsonDirty { get; set; }

    public DisplaySurfaceFingerprint? SurfaceFingerprint { get; set; }

    internal List<ChatMessage> PendingAppends { get; } = [];

    internal HashSet<string> PendingAppendIds { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Process-wide conversation manager: opened sessions stay in memory; disk is flushed
/// on a timer, shutdown, or structural mutations (compact / clear / delete / workspace).
/// </summary>
public sealed class SessionRuntimeStore : IConversationTranscriptWriter, IDisposable
{
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(15);

    private readonly IFileStorageService _storage;
    private readonly SessionUiCache? _uiCache;
    private readonly ConcurrentDictionary<string, RuntimeSessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionFlushLocks = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _flushCts = new();
    private readonly Task? _flushLoop;
    private bool _disposed;

    public SessionRuntimeStore(
        IFileStorageService storage,
        SessionUiCache? uiCache = null,
        bool enablePeriodicFlush = true,
        TimeSpan? flushInterval = null)
    {
        _storage = storage;
        _uiCache = uiCache;
        if (enablePeriodicFlush)
        {
            _flushLoop = RunFlushLoopAsync(flushInterval ?? DefaultFlushInterval, _flushCts.Token);
        }
    }

    public bool TryGetHydrated(string sessionId, out RuntimeSessionEntry entry)
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

        SessionTurnUiController? ui = null;
        var hasDisplayMessages = _uiCache is not null
            && _uiCache.TryGet(sessionId, out ui)
            && ui is { Messages.Count: > 0 };

        var cachedSurface = ui?.CaptureSurfaceSnapshot();
        if (hasDisplayMessages
            && cachedSurface is not null
            && found.Hydrated)
        {
            // Once a display surface has been hydrated, its per-session controller is
            // authoritative for the lifetime of this process. Streaming and turn
            // finalization update it before the periodic transcript flush, so a
            // fingerprint mismatch means the cached surface is newer than disk rather
            // than stale. Never cold-load an older snapshot over it.
            lock (_gate)
            {
                found.OlderDisplayCursor = cachedSurface.Value.OlderDisplayCursor;
                found.SurfaceFingerprint = cachedSurface.Value.Fingerprint;
                found.Hydrated = true;
            }

            entry = found;
            return true;
        }

        if (!found.Hydrated)
        {
            return false;
        }

        // Hydrated + empty UI is only reusable for a truly empty chat. A session with
        // in-memory history but no display messages must reload from conversation.jsonl.
        if (_uiCache is not null && found.Session.Messages.Count > 0)
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
        ConversationDisplayCursor? olderDisplayCursor,
        DisplaySurfaceFingerprint? surfaceFingerprint = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return;
        }

        lock (_gate)
        {
            entry.Hydrated = true;
            entry.OlderDisplayCursor = olderDisplayCursor;
            entry.SurfaceFingerprint = surfaceFingerprint;
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
            entry.SessionJsonDirty = false;
        }
    }

    public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(message.Id))
        {
            return Task.CompletedTask;
        }

        var entry = _sessions.GetOrAdd(sessionId, _ => new RuntimeSessionEntry());
        lock (_gate)
        {
            if (entry.PendingAppendIds.Add(message.Id))
            {
                entry.PendingAppends.Add(message);
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkSessionDirtyAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        var entry = Attach(session);
        lock (_gate)
        {
            entry.SessionJsonDirty = true;
        }

        return Task.CompletedTask;
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
            toSave = entry.SessionJsonDirty ? entry.Session : null;
            entry.SessionJsonDirty = false;
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

                    if (toSave is not null)
                    {
                        entry.SessionJsonDirty = true;
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
        _flushCts.Cancel();
        _flushCts.Dispose();
    }

    private async Task RunFlushLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await FlushAllAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Best-effort background flush; next tick retries.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }
}
