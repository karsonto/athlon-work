using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Debounces session index updates and upserts single entries instead of scanning all sessions.
/// </summary>
internal sealed class SessionIndexCoordinator
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(1500);

    private readonly IAppPathProvider _paths;
    private readonly IJsonFileStore _jsonFileStore;
    private readonly IAgentRunContextAccessor _runContextAccessor;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly object _scheduleLock = new();
    private readonly Dictionary<string, SessionIndexEntry> _pending = new(StringComparer.Ordinal);
    private CancellationTokenSource? _debounceCts;

    public SessionIndexCoordinator(IAppPathProvider paths, IJsonFileStore jsonFileStore, IAgentRunContextAccessor runContextAccessor)
    {
        _paths = paths;
        _jsonFileStore = jsonFileStore;
        _runContextAccessor = runContextAccessor;
    }

    public void ScheduleUpdate(AgentSession session)
    {
        if (_runContextAccessor.Current?.Kind == AgentRunKind.SubAgent)
        {
            return;
        }

        var sessionDir = _runContextAccessor.ResolveSessionDirectory(_paths.SessionsPath, session.Id);
        var entry = new SessionIndexEntry(session.Id, session.Title, sessionDir, session.UpdatedAt);
        ScheduleUpdate(entry);
    }

    public void ScheduleUpdate(SessionIndexEntry entry)
    {
        if (!SessionDirectoryLayout.IsEligibleForSessionMenu(_paths.SessionsPath, entry))
        {
            return;
        }

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
                if (cached is { Count: > 0 })
                {
                    var merged = MergePendingEntries(cached);
                    if (IsSessionIndexFresh(indexPath, merged))
                    {
                        return OrderMenuSessions(merged);
                    }
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
        if (!SessionDirectoryLayout.IsEligibleForSessionMenu(_paths.SessionsPath, entry))
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(_paths.SessionsPath);
            var indexPath = Path.Combine(_paths.SessionsPath, "index.json");
            var nestedSubAgentSessionIds = SessionDirectoryLayout.CollectNestedSubAgentSessionIds(_paths.SessionsPath);
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
                        if (SessionDirectoryLayout.IsEligibleForSessionMenu(
                                _paths.SessionsPath,
                                existing,
                                nestedSubAgentSessionIds))
                        {
                            entries[existing.Id] = existing;
                        }
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

    private IReadOnlyList<SessionIndexEntry> MergePendingEntries(IReadOnlyList<SessionIndexEntry> entries)
    {
        Dictionary<string, SessionIndexEntry>? pendingSnapshot = null;
        lock (_scheduleLock)
        {
            if (_pending.Count > 0)
            {
                pendingSnapshot = new Dictionary<string, SessionIndexEntry>(_pending, StringComparer.Ordinal);
            }
        }

        if (pendingSnapshot is null)
        {
            return entries;
        }

        var merged = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        foreach (var pending in pendingSnapshot.Values)
        {
            if (!merged.TryGetValue(pending.Id, out var existing) || pending.UpdatedAt >= existing.UpdatedAt)
            {
                merged[pending.Id] = pending;
            }
        }

        return merged.Values.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Id).ToArray();
    }

    private async Task<IReadOnlyList<SessionIndexEntry>> RebuildSessionIndexAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SessionIndexEntry>(StringComparer.Ordinal);
        if (!Directory.Exists(_paths.SessionsPath))
        {
            return Array.Empty<SessionIndexEntry>();
        }

        var nestedSubAgentSessionIds = SessionDirectoryLayout.CollectNestedSubAgentSessionIds(_paths.SessionsPath);
        foreach (var sessionJson in SessionDirectoryLayout.EnumerateTopLevelSessionJsonPaths(_paths.SessionsPath))
        {
            var indexEntry = SessionJsonIndexReader.TryRead(sessionJson);
            if (indexEntry is null
                || !SessionDirectoryLayout.IsEligibleForSessionMenu(
                    _paths.SessionsPath,
                    indexEntry,
                    nestedSubAgentSessionIds))
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
        var sessionsRoot = Path.GetDirectoryName(indexPath);
        if (string.IsNullOrWhiteSpace(sessionsRoot) || !Directory.Exists(sessionsRoot))
        {
            return true;
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

    private SessionIndexEntry[] OrderMenuSessions(IEnumerable<SessionIndexEntry> entries)
    {
        var nestedSubAgentSessionIds = SessionDirectoryLayout.CollectNestedSubAgentSessionIds(_paths.SessionsPath);
        return entries
            .Where(entry => SessionDirectoryLayout.IsEligibleForSessionMenu(
                _paths.SessionsPath,
                entry,
                nestedSubAgentSessionIds))
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Id)
            .ToArray();
    }
}
