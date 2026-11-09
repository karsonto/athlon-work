using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

public sealed class FileTeamDefinitionStore(IAppPathProvider paths, IJsonFileStore jsonFileStore) : ITeamDefinitionStore
{
    private string Root => Path.Combine(paths.ConfigPath, "teams");

    public async Task<IReadOnlyList<TeamDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Root);
        // Dedupe by Id in case stale/duplicate filenames exist on disk (keep newest UpdatedAt).
        var byId = new Dictionary<string, TeamDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            var team = await jsonFileStore.LoadAsync<TeamDefinition>(file, cancellationToken).ConfigureAwait(false);
            if (team is null || string.IsNullOrWhiteSpace(team.Id))
            {
                continue;
            }

            if (!byId.TryGetValue(team.Id, out var existing)
                || team.UpdatedAt >= existing.UpdatedAt)
            {
                byId[team.Id] = team;
            }
        }

        return byId.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<TeamDefinition?> GetAsync(string teamId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(teamId))
        {
            return null;
        }

        var path = GetPath(teamId);
        return File.Exists(path)
            ? await jsonFileStore.LoadAsync<TeamDefinition>(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task SaveAsync(TeamDefinition team, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(team);
        if (string.IsNullOrWhiteSpace(team.Id))
        {
            team.Id = IdGen.NewId();
        }

        team.UpdatedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Root);
        await jsonFileStore.SaveAsync(GetPath(team.Id), team, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string teamId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(teamId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetPath(string teamId) =>
        Path.Combine(Root, Sanitize(teamId) + ".json");

    private static string Sanitize(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            id = id.Replace(c, '_');
        }

        return id;
    }
}
