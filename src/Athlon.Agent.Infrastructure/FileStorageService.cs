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
    private readonly IAppLogger _logger = logger.ForContext("Storage");

    public string RootPath => paths.RootPath;

    public async Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        EnsureSessionLogDirectories(session.Id);
        var sessionDir = GetSessionDirectory(session);

        await jsonFileStore.SaveAsync(Path.Combine(sessionDir, "session.json"), session, cancellationToken);
        await AtomicFile.WriteAllTextAsync(Path.Combine(sessionDir, "conversation.md"), SessionMarkdownWriter.WriteConversation(session), cancellationToken);
        await RefreshIndexAsync(cancellationToken);
        _logger.Information("Session persisted to {SessionDir}", sessionDir);
    }

    public async Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default)
    {
        var summaryDir = Path.Combine(paths.SessionsPath, summary.SessionId, "summaries");
        Directory.CreateDirectory(summaryDir);
        await AtomicFile.WriteAllTextAsync(Path.Combine(summaryDir, $"{summary.Id}.md"), SessionMarkdownWriter.WriteSummary(summary), cancellationToken);
    }

    public async Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
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

    public async Task<string> SaveEvictedToolResultAsync(
        string sessionId,
        string toolCallId,
        string content,
        CancellationToken cancellationToken = default)
    {
        var evictedDir = Path.Combine(GetSessionDirectory(sessionId), "evicted");
        Directory.CreateDirectory(evictedDir);
        var path = Path.Combine(evictedDir, $"{toolCallId}.txt");
        await AtomicFile.WriteAllTextAsync(path, content, cancellationToken);
        return path;
    }

    public async Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        EnsureSessionLogDirectories(sessionId);
        var path = Path.Combine(GetSessionDirectory(sessionId), "conversation.jsonl");
        await jsonFileStore.AppendJsonLineAsync(
            path,
            new
            {
                time = message.CreatedAt,
                id = message.Id,
                role = message.Role.ToString(),
                parentId = message.ParentId,
                content = message.Content,
                imageAttachments = message.ImageAttachments
            },
            cancellationToken);
    }

    public async Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

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

    public async Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var directPath = Path.Combine(GetSessionDirectory(sessionId), "session.json");
        if (File.Exists(directPath))
        {
            return await jsonFileStore.LoadAsync<AgentSession>(directPath, cancellationToken);
        }

        if (!Directory.Exists(paths.SessionsPath))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(paths.SessionsPath, "session.json", SearchOption.AllDirectories))
        {
            var session = await jsonFileStore.LoadAsync<AgentSession>(file, cancellationToken);
            if (session is not null && string.Equals(session.Id, sessionId, StringComparison.Ordinal))
            {
                return session;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.SessionsPath))
        {
            return Array.Empty<SessionIndexEntry>();
        }

        var result = new Dictionary<string, SessionIndexEntry>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(paths.SessionsPath, "session.json", SearchOption.AllDirectories))
        {
            var session = await jsonFileStore.LoadAsync<AgentSession>(file, cancellationToken);
            if (session is null)
            {
                continue;
            }

            if (!result.TryGetValue(session.Id, out var existing) || session.UpdatedAt > existing.UpdatedAt)
            {
                result[session.Id] = new SessionIndexEntry(session.Id, session.Title, Path.GetDirectoryName(file)!, session.UpdatedAt);
            }
        }

        return result.Values.OrderByDescending(item => item.UpdatedAt).ToArray();
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
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "skills.json"), settings.Skills, cancellationToken);
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
        var index = await ListSessionsAsync(cancellationToken);
        Directory.CreateDirectory(paths.SessionsPath);
        await jsonFileStore.SaveAsync(Path.Combine(paths.SessionsPath, "index.json"), index, cancellationToken);
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
