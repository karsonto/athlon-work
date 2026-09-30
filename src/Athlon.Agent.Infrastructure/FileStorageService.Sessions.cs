using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

/// <summary>Session documents: save, load, list and delete.</summary>
public sealed partial class FileStorageService
{
    public async Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        string sessionDir;
        try
        {
            using (await SessionWriteLock.AcquireAsync(session.Id, cancellationToken).ConfigureAwait(false))
            {
                EnsureSessionLogDirectories(session.Id);
                sessionDir = GetSessionDirectory(session);

                await jsonFileStore.SaveAsync(Path.Combine(sessionDir, "session.json"), session, cancellationToken);
                _logger.Information("Session persisted to {SessionDir}", sessionDir);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await EnqueueStorageDiagnosticAsync(
                session.Id,
                RuntimeDiagnosticPhase.Persist,
                "storage.persist_failed",
                RuntimeDiagnosticSeverity.Error,
                RuntimeDiagnosticErrorCodes.StoragePersistFailed,
                ex.Message).ConfigureAwait(false);
            throw;
        }

        if (SessionDirectoryLayout.IsTopLevelSessionDirectory(paths.SessionsPath, sessionDir)
            && !SessionDirectoryLayout.IsNestedSubAgentSessionId(paths.SessionsPath, session.Id))
        {
            _indexCoordinator.ScheduleUpdate(session);
        }
    }

    public async Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default)
    {
        var summaryDir = Path.Combine(paths.SessionsPath, summary.SessionId, "summaries");
        Directory.CreateDirectory(summaryDir);
        await AtomicFile.WriteAllTextAsync(Path.Combine(summaryDir, $"{summary.Id}.md"), SessionMarkdownWriter.WriteSummary(summary), cancellationToken);
    }

    public async Task SaveSessionOffThreadAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        // Serialization is CPU work proportional to the whole message list, so it must not happen
        // on a caller that is painting. The write itself already runs on the pool.
        var json = await BackgroundFileIo.RunAsync(
            () => JsonSerializer.Serialize(session, JsonFileStore.Options)).ConfigureAwait(false);

        string sessionDir;
        try
        {
            using (await SessionWriteLock.AcquireAsync(session.Id, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSessionLogDirectoriesAsync(session.Id).ConfigureAwait(false);
                sessionDir = GetSessionDirectory(session);
                await FileIoRetry.RunAsync(
                    () => AtomicFile.WriteAllTextAsync(Path.Combine(sessionDir, "session.json"), json, cancellationToken),
                    cancellationToken);
                _logger.Information("Session persisted to {SessionDir}", sessionDir);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await EnqueueStorageDiagnosticAsync(
                session.Id,
                RuntimeDiagnosticPhase.Persist,
                "storage.persist_failed",
                RuntimeDiagnosticSeverity.Error,
                RuntimeDiagnosticErrorCodes.StoragePersistFailed,
                ex.Message).ConfigureAwait(false);
            throw;
        }

        if (SessionDirectoryLayout.IsTopLevelSessionDirectory(paths.SessionsPath, sessionDir)
            && !SessionDirectoryLayout.IsNestedSubAgentSessionId(paths.SessionsPath, session.Id))
        {
            _indexCoordinator.ScheduleUpdate(session);
        }
    }

    public Task<SessionIndexEntry?> LoadSessionIndexEntryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult<SessionIndexEntry?>(null);
        }

        if (SessionDirectoryLayout.IsNestedSubAgentSessionId(paths.SessionsPath, sessionId))
        {
            return Task.FromResult<SessionIndexEntry?>(null);
        }

        // Only the direct layout is eligible for the fast path; anything else (index fallback,
        // recovered/moved directories) returns null so the caller loads the full session instead
        // of rendering a metadata-only shell from a path we are not sure about.
        var directPath = Path.Combine(GetSessionDirectory(sessionId), "session.json");
        if (!File.Exists(directPath))
        {
            return Task.FromResult<SessionIndexEntry?>(null);
        }

        var entry = SessionJsonIndexReader.TryRead(directPath);
        return Task.FromResult(entry is null
            || !string.Equals(entry.Id, sessionId, StringComparison.Ordinal)
                ? null
                : entry);
    }

    public async Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        if (SessionDirectoryLayout.IsNestedSubAgentSessionId(paths.SessionsPath, sessionId))
        {
            return null;
        }

        try
        {
            var directPath = Path.Combine(GetSessionDirectory(sessionId), "session.json");
            if (File.Exists(directPath))
            {
                var session = await jsonFileStore.LoadAsync<AgentSession>(directPath, cancellationToken);
                return session is null ? null : ChatMessageMemorySanitizer.SanitizeSession(session);
            }

            if (!Directory.Exists(paths.SessionsPath))
            {
                return null;
            }

            var indexedEntry = (await ListSessionsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(entry => string.Equals(entry.Id, sessionId, StringComparison.Ordinal));
            if (indexedEntry is not null)
            {
                var indexedPath = Path.Combine(indexedEntry.Path, "session.json");
                if (File.Exists(indexedPath))
                {
                    var session = await jsonFileStore.LoadAsync<AgentSession>(indexedPath, cancellationToken);
                    return session is null ? null : ChatMessageMemorySanitizer.SanitizeSession(session);
                }
            }

            foreach (var sessionJson in SessionDirectoryLayout.EnumerateTopLevelSessionJsonPaths(paths.SessionsPath))
            {
                var indexEntry = SessionJsonIndexReader.TryRead(sessionJson);
                if (indexEntry is null || !string.Equals(indexEntry.Id, sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                var session = await jsonFileStore.LoadAsync<AgentSession>(sessionJson, cancellationToken);
                return session is null ? null : ChatMessageMemorySanitizer.SanitizeSession(session);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await EnqueueStorageDiagnosticAsync(
                sessionId,
                RuntimeDiagnosticPhase.Prepare,
                "storage.load_failed",
                RuntimeDiagnosticSeverity.Error,
                RuntimeDiagnosticErrorCodes.StorageLoadFailed,
                ex.Message).ConfigureAwait(false);
            throw;
        }

        return null;
    }

    public async Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        return await _indexCoordinator.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in await ListSessionsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(entry.Id, sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.Path) && Directory.Exists(entry.Path))
                {
                    await DeleteDirectoryResilientAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                    deleted.Add(entry.Path);
                }
            }

            var directDir = GetSessionDirectory(sessionId);
            if (Directory.Exists(directDir) && !deleted.Contains(directDir))
            {
                await DeleteDirectoryResilientAsync(directDir, cancellationToken).ConfigureAwait(false);
            }

            // A removed sub-agent session must stop resolving; the delete may have taken out the
            // last nested directory for this id.
            SessionDirectoryLayout.InvalidateNestedIndex(paths.SessionsPath);
        }

        await _indexCoordinator.RefreshIndexImmediateAsync(cancellationToken);
        SessionWriteLock.RemoveSession(sessionId);
        // The directory is gone, so the "already prepared" marker must go too: if the same id is
        // written again, its folders have to be recreated rather than assumed present. Keyed off the
        // plain top-level path because that is what a non-sub-agent context resolves to.
        _ensuredSessionDirs.TryRemove(Path.Combine(paths.SessionsPath, sessionId), out _);
        _logger.Information("Deleted session {SessionId}", sessionId);
    }

    /// <summary>
    /// <see cref="Directory.Delete(string, bool)"/> fails outright if any file inside is still open
    /// — a reader mid-deserialization, the search indexer, or antivirus. Retrying rides out those
    /// short-lived handles instead of surfacing a sharing violation and leaving the session
    /// half-deleted. Callers suppress their own background readers; this covers everything else.
    /// </summary>
    private static Task DeleteDirectoryResilientAsync(string path, CancellationToken cancellationToken) =>
        FileIoRetry.RunAsync(() =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            return Task.CompletedTask;
        }, cancellationToken);
}
