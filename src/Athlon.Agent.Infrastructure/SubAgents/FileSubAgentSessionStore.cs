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

        var meta = await jsonFileStore.LoadAsync<SubAgentMeta>(metaPath, cancellationToken);
        return meta?.Role;
    }

    private async Task SaveRoleAsync(string directory, string role, CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(directory, "meta.json");
        await jsonFileStore.SaveAsync(metaPath, new SubAgentMeta(role), cancellationToken);
    }

    private string GetSubAgentDirectory(string parentSessionId, string subSessionId) =>
        Path.Combine(paths.SessionsPath, parentSessionId, "subagents", "default", subSessionId);

    private sealed record SubAgentMeta(string Role);
}
