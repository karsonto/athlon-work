using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

public sealed class FileStorageService(
    IAppLogger logger,
    IAppPathProvider paths,
    IJsonFileStore jsonFileStore,
    IAgentRunContextAccessor runContextAccessor) : IFileStorageService
{
    private readonly IAppLogger _logger = logger.ForContext("Storage");
    private readonly SessionIndexCoordinator _indexCoordinator = new(paths, jsonFileStore, runContextAccessor);

    public string RootPath => paths.RootPath;

    public async Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        string sessionDir;
        using (await SessionWriteLock.AcquireAsync(session.Id, cancellationToken).ConfigureAwait(false))
        {
            EnsureSessionLogDirectories(session.Id);
            sessionDir = GetSessionDirectory(session);

            await jsonFileStore.SaveAsync(Path.Combine(sessionDir, "session.json"), session, cancellationToken);
            _logger.Information("Session persisted to {SessionDir}", sessionDir);
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

            await AtomicFile.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
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

            // conversation.jsonl is append-only, lines are already roughly chronological.
            // Use a list with duplicate tracking to avoid Dictionary+OrderBy overhead.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var messages = new List<ChatMessage>();
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var message = ConversationDisplayLog.TryParseLine(line);
                if (message is null || !seen.Add(message.Id))
                {
                    continue;
                }

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

            return ChatMessageMemorySanitizer.SanitizeMessages(messages);
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

    public async Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            EnsureSessionLogDirectories(sessionId);
            var path = Path.Combine(GetSessionDirectory(sessionId), "tool-calls", "calls.jsonl");
            await jsonFileStore.AppendJsonLineAsync(
                path,
                new
                {
                    time = entry.Timestamp,
                    toolCallId = entry.ToolCallId,
                    toolName = entry.ToolName,
                    arguments = entry.Arguments,
                    succeeded = entry.Succeeded,
                    summary = entry.Summary,
                    content = HttpLogSanitizer.Truncate(entry.Content),
                    error = entry.Error,
                    durationMs = entry.DurationMs
                },
                cancellationToken);
        }
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
        Directory.CreateDirectory(Path.Combine(sessionDir, "tool-calls"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "summaries"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "transcripts"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "evicted"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "http"));
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

    private string GetSessionTranscriptsDirectory(string sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "transcripts");
}
