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

public sealed class FileStorageService(IAppLogger logger, IAppPathProvider paths, IJsonFileStore jsonFileStore) : IFileStorageService
{
    private static readonly SemaphoreSlim IndexLock = new(1, 1);
    private readonly IAppLogger _logger = logger.ForContext("Storage");

    public string RootPath => paths.RootPath;

    public async Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        using (await SessionWriteLock.AcquireAsync(session.Id, cancellationToken).ConfigureAwait(false))
        {
            EnsureSessionLogDirectories(session.Id);
            var sessionDir = GetSessionDirectory(session);

            await jsonFileStore.SaveAsync(Path.Combine(sessionDir, "session.json"), session, cancellationToken);
            await AtomicFile.WriteAllTextAsync(
                Path.Combine(sessionDir, "conversation.md"),
                SessionMarkdownWriter.WriteConversation(session),
                cancellationToken);
            _logger.Information("Session persisted to {SessionDir}", sessionDir);
        }

        await RefreshIndexAsync(cancellationToken);
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

        var path = GetConversationDisplayPath(sessionId);
        if (!File.Exists(path))
        {
            return Array.Empty<ChatMessage>();
        }

        var byId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            var message = ConversationDisplayLog.TryParseLine(line);
            if (message is null)
            {
                continue;
            }

            byId[message.Id] = message;
        }

        var ordered = byId.Values.OrderBy(message => message.CreatedAt).ToArray();
        return ChatMessageMemorySanitizer.SanitizeMessages(ordered);
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

        foreach (var file in Directory.EnumerateFiles(paths.SessionsPath, "session.json", SearchOption.AllDirectories))
        {
            var indexEntry = SessionJsonIndexReader.TryRead(file);
            if (indexEntry is null || !string.Equals(indexEntry.Id, sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            var session = await jsonFileStore.LoadAsync<AgentSession>(file, cancellationToken);
            return session is null ? null : ChatMessageMemorySanitizer.SanitizeSession(session);
        }

        return null;
    }

    public async Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.SessionsPath))
        {
            return Array.Empty<SessionIndexEntry>();
        }

        var indexPath = Path.Combine(paths.SessionsPath, "index.json");
        if (File.Exists(indexPath))
        {
            var cached = await jsonFileStore.LoadAsync<List<SessionIndexEntry>>(indexPath, cancellationToken);
            if (cached is { Count: > 0 } && IsSessionIndexFresh(indexPath, cached))
            {
                return cached.OrderByDescending(item => item.UpdatedAt).ToArray();
            }
        }

        return await RebuildSessionIndexAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SessionIndexEntry>> RebuildSessionIndexAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SessionIndexEntry>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(paths.SessionsPath, "session.json", SearchOption.AllDirectories))
        {
            var entry = SessionJsonIndexReader.TryRead(file);
            if (entry is null)
            {
                continue;
            }

            if (!result.TryGetValue(entry.Id, out var existing) || entry.UpdatedAt > existing.UpdatedAt)
            {
                result[entry.Id] = entry;
            }
        }

        var ordered = result.Values.OrderByDescending(item => item.UpdatedAt).ToArray();
        Directory.CreateDirectory(paths.SessionsPath);
        await jsonFileStore.SaveAsync(Path.Combine(paths.SessionsPath, "index.json"), ordered, cancellationToken);
        return ordered;
    }

    private static bool IsSessionIndexFresh(string indexPath, IReadOnlyList<SessionIndexEntry> entries)
    {
        var indexTime = File.GetLastWriteTimeUtc(indexPath);
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

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in await ListSessionsAsync(cancellationToken))
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

        await RefreshIndexAsync(cancellationToken);
        _logger.Information("Deleted session {SessionId}", sessionId);
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Model.LegacyApiKeyCredentialName = null;
        Directory.CreateDirectory(paths.ConfigPath);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "settings.json"), settings, cancellationToken);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "models.json"), settings.Model, cancellationToken);
        await McpConfigFileService.SaveServersAsync(paths, settings.McpServers, cancellationToken);
        await SkillConfigFileService.SaveSkillsAsync(paths, settings.Skills, cancellationToken);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "workspaces.json"), settings.Workspaces, cancellationToken);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "logging.json"), settings.Logging, cancellationToken);
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

        var mcpServers = await McpConfigFileService.LoadServersAsync(paths, cancellationToken);
        if (mcpServers.Count > 0)
        {
            settings.McpServers = mcpServers;
        }

        var skills = await SkillConfigFileService.LoadSkillsAsync(paths, cancellationToken);
        if (skills.Count > 0)
        {
            settings.Skills = skills;
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

    private async Task RefreshIndexAsync(CancellationToken cancellationToken)
    {
        await IndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RebuildSessionIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IndexLock.Release();
        }
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

    private string GetSessionDirectory(string sessionId) => Path.Combine(paths.SessionsPath, sessionId);

    private string GetSessionTranscriptsDirectory(string sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "transcripts");
}
