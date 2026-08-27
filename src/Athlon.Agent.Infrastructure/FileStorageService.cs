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

public sealed class FileStorageService(
    IAppLogger logger,
    IAppPathProvider paths,
    IJsonFileStore jsonFileStore,
    IAgentRunContextAccessor runContextAccessor,
    IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null) : IFileStorageService
{
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private readonly IAppLogger _logger = logger.ForContext("Storage");
    private readonly SessionIndexCoordinator _indexCoordinator = new(paths, jsonFileStore, runContextAccessor);
    private readonly IRuntimeDiagnosticEventSink? _runtimeDiagnosticEventSink = runtimeDiagnosticEventSink;
    private ToolCallLogWriteQueue? _toolCallLogQueue;

    private ToolCallLogWriteQueue ToolCallLogQueue =>
        _toolCallLogQueue ??= new ToolCallLogWriteQueue(WriteToolCallLogCoreAsync, _logger);

    public string RootPath => paths.RootPath;

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

    public async Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var transcriptDir = GetSessionTranscriptsDirectory(sessionId);
            Directory.CreateDirectory(transcriptDir);
            var path = Path.Combine(transcriptDir, $"transcript_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.jsonl");

            var builder = new StringBuilder();
            foreach (var message in messages)
            {
                builder.AppendLine(JsonSerializer.Serialize(message, JsonFileStore.JsonLineOptions));
            }

            await FileIoRetry.RunAsync(
                () => File.WriteAllTextAsync(path, builder.ToString(), Utf8Bom, cancellationToken),
                cancellationToken);
            return path;
        }
    }

    public async Task<string> SaveEvictedToolResultAsync(
        string sessionId,
        string toolCallId,
        string content,
        CancellationToken cancellationToken = default)
    {
        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var evictedDir = Path.Combine(GetSessionDirectory(sessionId), "evicted");
            Directory.CreateDirectory(evictedDir);
            var path = Path.Combine(evictedDir, $"{toolCallId}.txt");
            await AtomicFile.WriteAllTextAsync(path, content, cancellationToken);
            return path;
        }
    }

    public async Task<string?> TryReadEvictedToolResultAsync(
        string sessionId,
        string toolCallId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(toolCallId))
        {
            return null;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = Path.Combine(GetSessionDirectory(sessionId), "evicted", $"{toolCallId}.txt");
            if (!File.Exists(path))
            {
                return null;
            }

            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ChatMessage?> TryLoadConversationMessageAsync(
        string sessionId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = GetConversationDisplayPath(sessionId);
            if (!File.Exists(path))
            {
                return null;
            }

            ChatMessage? latest = null;
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var message = ConversationDisplayLog.TryParseLine(line);
                if (message is null || !string.Equals(message.Id, messageId, StringComparison.Ordinal))
                {
                    continue;
                }

                latest = message;
            }

            return latest;
        }
    }

    public async Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            EnsureSessionLogDirectories(sessionId);
            var path = GetConversationDisplayPath(sessionId);
            await jsonFileStore.AppendJsonLineAsync(path, message, cancellationToken);
        }
    }

    public async Task ReplaceConversationDisplayAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            EnsureSessionLogDirectories(sessionId);
            var path = GetConversationDisplayPath(sessionId);
            var builder = new StringBuilder();
            foreach (var message in messages)
            {
                builder.AppendLine(JsonSerializer.Serialize(message, JsonFileStore.JsonLineOptions));
            }

            await FileIoRetry.RunAsync(
                () => File.WriteAllTextAsync(path, builder.ToString(), Utf8Bom, cancellationToken),
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<ChatMessage>();
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = GetConversationDisplayPath(sessionId);
            if (!File.Exists(path))
            {
                return Array.Empty<ChatMessage>();
            }

            // conversation.jsonl is append-only. Later lines with the same Id win so a
            // mid-turn streaming checkpoint can be overwritten by the final Persist.
            var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
            var messages = new List<ChatMessage>();
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var message = ConversationDisplayLog.TryParseLine(line);
                if (message is null)
                {
                    continue;
                }

                if (indexById.TryGetValue(message.Id, out var existingIndex))
                {
                    messages[existingIndex] = message;
                    continue;
                }

                indexById[message.Id] = messages.Count;
                messages.Add(message);
            }

            // The file is append-only so order is stable; only sort if somehow out of order.
            if (messages.Count > 1)
            {
                for (var i = 1; i < messages.Count; i++)
                {
                    if (messages[i].CreatedAt < messages[i - 1].CreatedAt)
                    {
                        messages.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
                        break;
                    }
                }
            }

            // Strip heavy tool result content for display only (full content remains in conversation.jsonl
            // for model context reconstruction).
            for (var i = 0; i < messages.Count; i++)
            {
                messages[i] = StripToolContentForDisplay(messages[i]);
            }

            return ChatMessageMemorySanitizer.SanitizeMessages(messages);
        }
    }

    public async Task<ConversationDisplayPage> LoadConversationDisplayPageAsync(
        string sessionId,
        ConversationDisplayCursor? cursor = null,
        int pageSize = ConversationDisplayLimits.PageSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new ConversationDisplayPage(Array.Empty<ChatMessage>(), null);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = GetConversationDisplayPath(sessionId);
            if (!File.Exists(path))
            {
                return new ConversationDisplayPage(Array.Empty<ChatMessage>(), null);
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            var endOffset = cursor is null
                ? stream.Length
                : Math.Clamp(cursor.ByteOffset, 0, stream.Length);
            var seen = new HashSet<string>(
                cursor?.SeenMessageIds ?? Array.Empty<string>(),
                StringComparer.Ordinal);
            var page = new List<ChatMessage>(pageSize);
            var reversedLine = new List<byte>();
            var buffer = new byte[16 * 1024];
            var position = endOffset;
            long? olderOffset = null;

            while (position > 0 && page.Count < pageSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readStart = Math.Max(0, position - buffer.Length);
                var readLength = checked((int)(position - readStart));
                stream.Position = readStart;
                await stream.ReadExactlyAsync(buffer.AsMemory(0, readLength), cancellationToken)
                    .ConfigureAwait(false);

                for (var i = readLength - 1; i >= 0; i--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (buffer[i] != (byte)'\n')
                    {
                        reversedLine.Add(buffer[i]);
                        continue;
                    }

                    AddReverseLine(reversedLine, page, seen);
                    reversedLine.Clear();
                    if (page.Count == pageSize)
                    {
                        olderOffset = readStart + i;
                        break;
                    }
                }

                position = readStart;
            }

            if (position == 0 && page.Count < pageSize && reversedLine.Count > 0)
            {
                AddReverseLine(reversedLine, page, seen);
                reversedLine.Clear();
            }

            page.Reverse();
            EnsureChronologicalOrder(page);
            for (var i = 0; i < page.Count; i++)
            {
                page[i] = StripToolContentForDisplay(page[i]);
            }

            var sanitized = ChatMessageMemorySanitizer.SanitizeMessages(page);
            var nextCursor = olderOffset is > 0
                ? new ConversationDisplayCursor(olderOffset.Value, seen.ToArray())
                : null;
            return new ConversationDisplayPage(sanitized, nextCursor);
        }
    }

    private static void AddReverseLine(
        List<byte> reversedLine,
        List<ChatMessage> messages,
        HashSet<string> seen)
    {
        if (reversedLine.Count == 0)
        {
            return;
        }

        reversedLine.Reverse();
        var count = reversedLine.Count;
        if (count > 0 && reversedLine[count - 1] == (byte)'\r')
        {
            count--;
        }

        var line = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(reversedLine)[..count]);
        if (line.Length > 0 && line[0] == '\uFEFF')
        {
            line = line[1..];
        }

        var message = ConversationDisplayLog.TryParseLine(line);
        // Reverse scan meets newer lines first, so first-seen Id is last-wins in file order.
        if (message is not null && seen.Add(message.Id))
        {
            messages.Add(message);
        }
    }

    private static void EnsureChronologicalOrder(List<ChatMessage> messages)
    {
        for (var i = 1; i < messages.Count; i++)
        {
            if (messages[i].CreatedAt >= messages[i - 1].CreatedAt)
            {
                continue;
            }

            messages.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
            return;
        }
    }

    public async Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = GetConversationDisplayPath(sessionId);
            if (File.Exists(path))
            {
                await FileIoRetry.RunAsync(
                    () => AtomicFile.WriteAllTextAsync(path, string.Empty, cancellationToken),
                    cancellationToken);
            }
        }
    }

    private string GetConversationDisplayPath(string sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "conversation.jsonl");

    public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task AppendAttemptEventAsync(
        string sessionId,
        AgentAttemptEvent entry,
        CancellationToken cancellationToken = default)
    {
        try
        {
            BehaviorEventManager.Instance.RecordAttempt(entry);
        }
        catch
        {
            // Behavior reporting must never affect persistence.
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AgentAttemptEvent>> LoadAttemptEventsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = Path.Combine(GetSessionDirectory(sessionId), "attempts.jsonl");
            if (!File.Exists(path))
            {
                return Array.Empty<AgentAttemptEvent>();
            }

            var events = new List<AgentAttemptEvent>();
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var item = JsonSerializer.Deserialize<AgentAttemptEvent>(line, JsonFileStore.JsonLineOptions);
                    if (item is not null)
                    {
                        events.Add(item);
                    }
                }
            }
            return events;
        }
    }

    public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private Task WriteToolCallLogCoreAsync(
        string sessionId,
        SessionToolCallLogEntry entry,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
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
                    Directory.Delete(entry.Path, true);
                    deleted.Add(entry.Path);
                }
            }

            var directDir = GetSessionDirectory(sessionId);
            if (Directory.Exists(directDir) && !deleted.Contains(directDir))
            {
                Directory.Delete(directDir, true);
            }
        }

        await _indexCoordinator.RefreshIndexImmediateAsync(cancellationToken);
        SessionWriteLock.RemoveSession(sessionId);
        _logger.Information("Deleted session {SessionId}", sessionId);
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Model.LegacyApiKeyCredentialName = null;
        Directory.CreateDirectory(paths.ConfigPath);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "settings.json"), settings, cancellationToken);
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var path = Path.Combine(paths.ConfigPath, "settings.json");
        var settings = await jsonFileStore.LoadAsync<AppSettings>(path, cancellationToken);
        if (settings is null)
        {
            var defaults = CreateDefaultSettings();
            await SaveSettingsAsync(defaults, cancellationToken);
            return defaults;
        }

        if (RemoveLegacyMyDocumentsWorkspace(settings))
        {
            await SaveSettingsAsync(settings, cancellationToken);
        }

        return settings;
    }

    private static AppSettings CreateDefaultSettings() => new();

    private static bool RemoveLegacyMyDocumentsWorkspace(AppSettings settings)
    {
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var removed = settings.Workspaces.RemoveAll(workspace =>
            !string.IsNullOrWhiteSpace(workspace.RootPath)
            &&
            string.Equals(Path.GetFullPath(workspace.RootPath), Path.GetFullPath(myDocuments), StringComparison.OrdinalIgnoreCase));

        return removed > 0;
    }

    private void EnsureSessionLogDirectories(string sessionId)
    {
        var sessionDir = GetSessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(Path.Combine(sessionDir, "summaries"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "transcripts"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "evicted"));
    }

    private string GetSessionDirectory(AgentSession session) => GetSessionDirectory(session.Id);

    private string GetSessionDirectory(string sessionId)
    {
        var resolved = runContextAccessor.ResolveSessionDirectory(paths.SessionsPath, sessionId);
        if (runContextAccessor.Current?.Kind == AgentRunKind.SubAgent)
        {
            return resolved;
        }

        if (SessionDirectoryLayout.IsTopLevelSessionDirectory(paths.SessionsPath, resolved)
            && SessionDirectoryLayout.TryFindNestedSubAgentDirectory(paths.SessionsPath, sessionId) is { } nested)
        {
            return nested;
        }

        return resolved;
    }

    private async Task EnqueueStorageDiagnosticAsync(
        string? sessionId,
        RuntimeDiagnosticPhase phase,
        string eventType,
        RuntimeDiagnosticSeverity severity,
        string errorCode,
        string? message)
    {
        if (_runtimeDiagnosticEventSink is not { } sink)
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

    private string GetSessionTranscriptsDirectory(string sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "transcripts");

    private static ChatMessage StripToolContentForDisplay(ChatMessage message) =>
        ConversationDisplayContentStripper.StripToolContentForDisplay(message);
}
