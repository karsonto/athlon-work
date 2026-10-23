using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.Storage;

namespace Athlon.Agent.Infrastructure.Storage.Repositories;

public sealed class SessionStorageFileRepository(
    IAppLogger logger,
    IAppPathProvider paths,
    IJsonFileStore jsonFileStore,
    IAgentRunContextAccessor runContextAccessor,
    SessionDirectoryService sessionDirectories,
    SessionIndexCoordinator indexCoordinator,
    IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null) : ISessionStorageRepository
{
    private readonly IAppLogger _logger = logger.ForContext("Storage");

    public async Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        string sessionDir;
        try
        {
            using (await SessionWriteLock.AcquireAsync(session.Id, cancellationToken).ConfigureAwait(false))
            {
                sessionDirectories.EnsureSessionLogDirectories(session.Id);
                sessionDir = sessionDirectories.GetSessionDirectory(session);

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
            indexCoordinator.ScheduleUpdate(session);
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
        var json = await BackgroundFileIo.RunAsync(
            () => JsonSerializer.Serialize(session, JsonFileStore.Options)).ConfigureAwait(false);

        string sessionDir;
        try
        {
            using (await SessionWriteLock.AcquireAsync(session.Id, cancellationToken).ConfigureAwait(false))
            {
                await sessionDirectories.EnsureSessionLogDirectoriesAsync(session.Id).ConfigureAwait(false);
                sessionDir = sessionDirectories.GetSessionDirectory(session);
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
            indexCoordinator.ScheduleUpdate(session);
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

        var directPath = Path.Combine(sessionDirectories.GetSessionDirectory(sessionId), "session.json");
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
            var directPath = Path.Combine(sessionDirectories.GetSessionDirectory(sessionId), "session.json");
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
        return await indexCoordinator.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
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

            var directDir = sessionDirectories.GetSessionDirectory(sessionId);
            if (Directory.Exists(directDir) && !deleted.Contains(directDir))
            {
                await DeleteDirectoryResilientAsync(directDir, cancellationToken).ConfigureAwait(false);
            }

            SessionDirectoryLayout.InvalidateNestedIndex(paths.SessionsPath);
        }

        await indexCoordinator.RefreshIndexImmediateAsync(cancellationToken);
        SessionWriteLock.RemoveSession(sessionId);
        sessionDirectories.TryForgetEnsuredDir(sessionId);
        _logger.Information("Deleted session {SessionId}", sessionId);
    }

    private static Task DeleteDirectoryResilientAsync(string path, CancellationToken cancellationToken) =>
        FileIoRetry.RunAsync(() =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            return Task.CompletedTask;
        }, cancellationToken);

    private async Task EnqueueStorageDiagnosticAsync(
        string? sessionId,
        RuntimeDiagnosticPhase phase,
        string eventType,
        RuntimeDiagnosticSeverity severity,
        string errorCode,
        string? message)
    {
        if (runtimeDiagnosticEventSink is not { } sink)
        {
            return;
        }

        var context = runContextAccessor.Current;
        var evt = new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: sessionId,
            runId: context?.RunId ?? sessionId,
            turnId: null,
            attemptId: null,
            parentAttemptId: null,
            toolCallId: null,
            messageId: null,
            component: RuntimeDiagnosticComponent.Storage,
            phase: phase,
            eventType: eventType,
            severity: severity,
            errorCode: errorCode,
            message: message);
        await sink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);
    }
}
