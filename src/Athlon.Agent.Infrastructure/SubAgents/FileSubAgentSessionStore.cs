using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class FileSubAgentSessionStore(IAppPathProvider paths, IJsonFileStore jsonFileStore) : ISubAgentSessionStore
{
    private static readonly JsonSerializerOptions MetaOptions = new(JsonFileStore.Options)
    {
        WriteIndented = true
    };

    public async Task<SubAgentSessionBundle?> LoadAsync(
        string parentSessionId,
        string subSessionId,
        CancellationToken cancellationToken = default)
    {
        var directory = GetSubAgentDirectory(parentSessionId, subSessionId);
        var sessionPath = Path.Combine(directory, "session.json");
        if (!File.Exists(sessionPath))
        {
            return null;
        }

        var session = await jsonFileStore.LoadAsync<AgentSession>(sessionPath, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var role = await LoadRoleAsync(directory, cancellationToken) ?? string.Empty;
        return new SubAgentSessionBundle(session, role);
    }

    public async Task SaveAsync(
        string parentSessionId,
        string subSessionId,
        SubAgentSessionBundle bundle,
        CancellationToken cancellationToken = default)
    {
        var directory = GetSubAgentDirectory(parentSessionId, subSessionId);
        Directory.CreateDirectory(directory);
        await jsonFileStore.SaveAsync(Path.Combine(directory, "session.json"), bundle.Session, cancellationToken);
        await SaveRoleAsync(directory, bundle.Role, cancellationToken);
    }

    private async Task<string?> LoadRoleAsync(string directory, CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(directory, "meta.json");
        if (!File.Exists(metaPath))
        {
            return null;
        }

        var meta = await jsonFileStore.LoadAsync<SubAgentMetaFile>(metaPath, cancellationToken);
        return meta?.Role;
    }

    private async Task SaveRoleAsync(string directory, string role, CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(directory, "meta.json");
        var existing = await jsonFileStore.LoadAsync<SubAgentMetaFile>(metaPath, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var meta = existing ?? new SubAgentMetaFile
        {
            SpawnRunId = $"run_{Guid.NewGuid():N}",
            CreatedAt = now,
            LastActivityAt = now
        };
        meta.Role = role;
        meta.LastActivityAt = now;
        await jsonFileStore.SaveAsync(metaPath, meta, cancellationToken).ConfigureAwait(false);
    }

    private string GetSubAgentDirectory(string parentSessionId, string subSessionId) =>
        Path.Combine(paths.SessionsPath, parentSessionId, "subagents", "default", subSessionId);
}
