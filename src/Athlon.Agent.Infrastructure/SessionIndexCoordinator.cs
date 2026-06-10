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
                    foreach (var existing in cached)
                    {
                        entries[existing.Id] = existing;
                    }
                }
            }

            entries[entry.Id] = entry;
            var ordered = entries.Values.OrderByDescending(item => item.UpdatedAt).ToArray();
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

    private async Task RebuildSessionIndexAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SessionIndexEntry>(StringComparer.Ordinal);
        if (!Directory.Exists(_paths.SessionsPath))
        {
            return;
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

        var ordered = result.Values.OrderByDescending(item => item.UpdatedAt).ToArray();
        Directory.CreateDirectory(_paths.SessionsPath);
        await _jsonFileStore.SaveAsync(Path.Combine(_paths.SessionsPath, "index.json"), ordered, cancellationToken)
            .ConfigureAwait(false);
    }
}
