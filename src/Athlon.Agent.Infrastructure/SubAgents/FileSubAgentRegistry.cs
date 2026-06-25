using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class FileSubAgentRegistry(IAppPathProvider paths, IJsonFileStore jsonFileStore) : ISubAgentRegistry
{
    private const string SubAgentsFolder = "subagents";
    private const string DefaultKind = "default";

    public async Task<SubAgentSessionEntry?> FindByLabelAsync(
        string parentSessionId,
        string label,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var sessions = await ListAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
        return sessions.FirstOrDefault(entry =>
            string.Equals(entry.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SubAgentSessionEntry?> FindBySubSessionIdAsync(
        string parentSessionId,
        string subSessionId,
        CancellationToken cancellationToken = default)
    {
        var directory = GetSubAgentDirectory(parentSessionId, subSessionId);
        var metaPath = Path.Combine(directory, "meta.json");
        var sessionPath = Path.Combine(directory, "session.json");
        if (!File.Exists(metaPath) && !File.Exists(sessionPath))
        {
            return null;
        }

        return await BuildEntryAsync(parentSessionId, subSessionId, directory, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SubAgentSessionEntry?> FindBySessionKeyAsync(
        string sessionKey,
        CancellationToken cancellationToken = default)
    {
        if (!SubAgentSessionKey.TryParse(sessionKey, out var parentSessionId, out var subSessionId))
        {
            return null;
        }

        return await FindBySubSessionIdAsync(parentSessionId, subSessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubAgentSessionEntry>> ListAsync(
        string parentSessionId,
        CancellationToken cancellationToken = default)
    {
        var root = GetSubAgentsRoot(parentSessionId);
        if (!Directory.Exists(root))
        {
            return Array.Empty<SubAgentSessionEntry>();
        }

        var entries = new List<SubAgentSessionEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var subSessionId = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(subSessionId))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(directory, "meta.json"))
                && !File.Exists(Path.Combine(directory, "session.json")))
            {
                continue;
            }

            var entry = await BuildEntryAsync(parentSessionId, subSessionId, directory, cancellationToken).ConfigureAwait(false);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries.OrderByDescending(entry => entry.LastActivityAt).ToArray();
    }

    public async Task RegisterAsync(
        string parentSessionId,
        string subSessionId,
        SubAgentMetaFile meta,
        CancellationToken cancellationToken = default)
    {
        var directory = GetSubAgentDirectory(parentSessionId, subSessionId);
        Directory.CreateDirectory(directory);
        await jsonFileStore.SaveAsync(Path.Combine(directory, "meta.json"), meta, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateLastActivityAsync(
        string parentSessionId,
        string subSessionId,
        CancellationToken cancellationToken = default)
    {
        var directory = GetSubAgentDirectory(parentSessionId, subSessionId);
        var metaPath = Path.Combine(directory, "meta.json");
        var meta = await jsonFileStore.LoadAsync<SubAgentMetaFile>(metaPath, cancellationToken).ConfigureAwait(false)
            ?? new SubAgentMetaFile();
        meta.LastActivityAt = DateTimeOffset.UtcNow;
        await jsonFileStore.SaveAsync(metaPath, meta, cancellationToken).ConfigureAwait(false);
    }

    public string GetSessionFilePath(string parentSessionId, string subSessionId) =>
        Path.Combine(GetSubAgentDirectory(parentSessionId, subSessionId), "session.json");

    public string GetSubAgentDirectory(string parentSessionId, string subSessionId) =>
        Path.Combine(paths.SessionsPath, parentSessionId, SubAgentsFolder, DefaultKind, subSessionId);

    private string GetSubAgentsRoot(string parentSessionId) =>
        Path.Combine(paths.SessionsPath, parentSessionId, SubAgentsFolder, DefaultKind);

    private async Task<SubAgentSessionEntry?> BuildEntryAsync(
        string parentSessionId,
        string subSessionId,
        string directory,
        CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(directory, "meta.json");
        var meta = await jsonFileStore.LoadAsync<SubAgentMetaFile>(metaPath, cancellationToken).ConfigureAwait(false)
            ?? new SubAgentMetaFile { Role = string.Empty };

        var session = await jsonFileStore.LoadAsync<AgentSession>(
            Path.Combine(directory, "session.json"),
            cancellationToken).ConfigureAwait(false);

        return new SubAgentSessionEntry(
            SubAgentSessionKey.Build(parentSessionId, subSessionId),
            subSessionId,
            parentSessionId,
            meta.Role ?? string.Empty,
            meta.Label,
            meta.SpawnRunId ?? string.Empty,
            meta.CreatedAt == default ? DateTimeOffset.UtcNow : meta.CreatedAt,
            meta.LastActivityAt == default ? DateTimeOffset.UtcNow : meta.LastActivityAt,
            Path.Combine(directory, "session.json"),
            session?.Messages.Count ?? 0);
    }
}
