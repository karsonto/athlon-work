using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

public sealed class FileAgentProfileStore(IAppPathProvider paths, IJsonFileStore jsonFileStore) : IAgentProfileStore
{
    private string Root => Path.Combine(paths.ConfigPath, "agent-profiles");

    public async Task<IReadOnlyList<AgentProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Root);
        var results = new List<AgentProfile>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            var profile = await jsonFileStore.LoadAsync<AgentProfile>(file, cancellationToken).ConfigureAwait(false);
            if (profile is not null && !string.IsNullOrWhiteSpace(profile.Id))
            {
                results.Add(profile);
            }
        }

        return results.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<AgentProfile?> GetAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        var path = GetPath(profileId);
        return File.Exists(path)
            ? await jsonFileStore.LoadAsync<AgentProfile>(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task SaveAsync(AgentProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            profile.Id = IdGen.NewId();
        }

        Directory.CreateDirectory(Root);
        await jsonFileStore.SaveAsync(GetPath(profile.Id), profile, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(profileId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetPath(string profileId) =>
        Path.Combine(Root, Sanitize(profileId) + ".json");

    private static string Sanitize(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            id = id.Replace(c, '_');
        }

        return id;
    }
}
