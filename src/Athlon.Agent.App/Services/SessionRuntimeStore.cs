using System.Collections.Concurrent;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed class RuntimeSessionEntry
{
    public AgentSession? Session { get; set; }

    public bool Hydrated { get; set; }

    public ConversationDisplayCursor? OlderDisplayCursor { get; set; }

    public bool SessionJsonDirty { get; set; }

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

        var hasDisplayMessages = _uiCache is not null
            && _uiCache.TryGet(sessionId, out var ui)
            && ui is { Messages.Count: > 0 };

        if (hasDisplayMessages)
        {
            found.Hydrated = true;
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

    public void MarkHydrated(string sessionId, ConversationDisplayCursor? olderDisplayCursor)
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

    public void UpdateSession(AgentSession session) => Attach(session);

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

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _storage.AppendConversationMessageAsync(sessionId, message, cancellationToken)
                .ConfigureAwait(false);
        }

        if (toSave is not null)
        {
            await _storage.SaveSessionAsync(toSave, cancellationToken).ConfigureAwait(false);
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
