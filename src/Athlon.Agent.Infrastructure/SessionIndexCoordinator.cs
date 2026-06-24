using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Debounces session index updates and upserts single entries instead of scanning all sessions.
/// </summary>
internal sealed class SessionIndexCoordinator
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(1500);

    private readonly IAppPathProvider _paths;
    private readonly IJsonFileStore _jsonFileStore;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly object _scheduleLock = new();
    private readonly Dictionary<string, SessionIndexEntry> _pending = new(StringComparer.Ordinal);
    private CancellationTokenSource? _debounceCts;

    public SessionIndexCoordinator(IAppPathProvider paths, IJsonFileStore jsonFileStore)
    {
        _paths = paths;
        _jsonFileStore = jsonFileStore;
    }

    public void ScheduleUpdate(AgentSession session)
    {
        var sessionDir = AmbientSubAgentStorageScope.ResolveSessionDirectory(_paths.SessionsPath, session.Id);
        var entry = new SessionIndexEntry(session.Id, session.Title, sessionDir, session.UpdatedAt);
        ScheduleUpdate(entry);
    }

    public void ScheduleUpdate(SessionIndexEntry entry)
    {
        lock (_scheduleLock)
        {
            _pending[entry.Id] = entry;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = FlushPendingAsync(token);
        }
    }

    public async Task RefreshIndexImmediateAsync(CancellationToken cancellationToken = default)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RebuildSessionIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_paths.SessionsPath))
        {
            return Array.Empty<SessionIndexEntry>();
        }

        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var indexPath = Path.Combine(_paths.SessionsPath, "index.json");
            if (File.Exists(indexPath))
            {
                var cached = await _jsonFileStore.LoadAsync<List<SessionIndexEntry>>(indexPath, cancellationToken)
                    .ConfigureAwait(false);
                if (cached is { Count: > 0 } && IsSessionIndexFresh(indexPath, cached))
                {
                    return FilterTopLevelSessions(cached);
                }
            }

            return await RebuildSessionIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceInterval, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Dictionary<string, SessionIndexEntry> batch;
        lock (_scheduleLock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = new Dictionary<string, SessionIndexEntry>(_pending, StringComparer.Ordinal);
            _pending.Clear();
        }

        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var entry in batch.Values)
            {
                if (!await TryUpsertIndexEntryAsync(entry, cancellationToken).ConfigureAwait(false))
                {
                    await RebuildSessionIndexAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<bool> TryUpsertIndexEntryAsync(SessionIndexEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_paths.SessionsPath);
            var indexPath = Path.Combine(_paths.SessionsPath, "index.json");
            var entries = new Dictionary<string, SessionIndexEntry>(StringComparer.Ordinal);

            if (File.Exists(indexPath))
            {
                var cached = await _jsonFileStore.LoadAsync<List<SessionIndexEntry>>(indexPath, cancellationToken)
                    .ConfigureAwait(false);
                if (cached is not null)
                {
                    if (!IsSessionIndexFresh(indexPath, cached))
                    {
                        return false;
                    }

                    foreach (var existing in cached)
                    {
                        entries[existing.Id] = existing;
                    }
                }
            }

            if (!entries.TryGetValue(entry.Id, out var existingEntry) || entry.UpdatedAt >= existingEntry.UpdatedAt)
            {
                entries[entry.Id] = entry;
            }

            var ordered = entries.Values.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Id).ToArray();
            await _jsonFileStore.SaveAsync(indexPath, ordered, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<SessionIndexEntry>> RebuildSessionIndexAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SessionIndexEntry>(StringComparer.Ordinal);
        if (!Directory.Exists(_paths.SessionsPath))
        {
            return Array.Empty<SessionIndexEntry>();
        }

        foreach (var file in Directory.EnumerateFiles(_paths.SessionsPath, "session.json", SearchOption.AllDirectories))
        {
            if (AmbientSubAgentStorageScope.IsSubAgentSessionPath(file))
            {
                continue;
            }

            var indexEntry = SessionJsonIndexReader.TryRead(file);
            if (indexEntry is null)
            {
                continue;
            }

            if (!result.TryGetValue(indexEntry.Id, out var existing) || indexEntry.UpdatedAt > existing.UpdatedAt)
            {
                result[indexEntry.Id] = indexEntry;
            }
        }

        var ordered = result.Values.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Id).ToArray();
        Directory.CreateDirectory(_paths.SessionsPath);
        await _jsonFileStore.SaveAsync(Path.Combine(_paths.SessionsPath, "index.json"), ordered, cancellationToken)
            .ConfigureAwait(false);
        return ordered;
    }

    private static bool IsSessionIndexFresh(string indexPath, IReadOnlyList<SessionIndexEntry> entries)
    {
        var indexTime = File.GetLastWriteTimeUtc(indexPath);
        var indexedSessionJsonPaths = entries
            .Select(entry => Path.GetFullPath(Path.Combine(entry.Path, "session.json")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sessionsRoot = Path.GetDirectoryName(indexPath);
        if (string.IsNullOrWhiteSpace(sessionsRoot) || !Directory.Exists(sessionsRoot))
        {
            return true;
        }

        foreach (var sessionJson in Directory.EnumerateFiles(sessionsRoot, "session.json", SearchOption.AllDirectories))
        {
            if (AmbientSubAgentStorageScope.IsSubAgentSessionPath(sessionJson))
            {
                continue;
            }

            if (!indexedSessionJsonPaths.Contains(Path.GetFullPath(sessionJson)))
            {
                return false;
            }

            if (File.GetLastWriteTimeUtc(sessionJson) > indexTime)
            {
                return false;
            }
        }

        foreach (var entry in entries)
        {
            var sessionJson = Path.Combine(entry.Path, "session.json");
            if (!File.Exists(sessionJson))
            {
                continue;
            }

            if (File.GetLastWriteTimeUtc(sessionJson) > indexTime)
            {
                return false;
            }
        }

        return true;
    }

    private static SessionIndexEntry[] FilterTopLevelSessions(IEnumerable<SessionIndexEntry> entries) =>
        entries
            .Where(entry => !AmbientSubAgentStorageScope.IsSubAgentSessionPath(Path.Combine(entry.Path, "session.json")))
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Id)
            .ToArray();
}
