using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

public sealed class FileTeamTaskStore(IAppPathProvider paths, IJsonFileStore jsonFileStore) : ITeamTaskStore
{
    public string ResolveTaskDirectory(string rootSessionId, string taskId) =>
        Path.Combine(paths.SessionsPath, rootSessionId, "teams", taskId);

    public string ResolveMemberDirectory(string rootSessionId, string taskId, string memberSessionId) =>
        Path.Combine(ResolveTaskDirectory(rootSessionId, taskId), "members", memberSessionId);

    public async Task<TeamTask?> GetAsync(string rootSessionId, string taskId, CancellationToken cancellationToken = default)
    {
        var path = GetTaskPath(rootSessionId, taskId);
        if (!File.Exists(path))
        {
            return null;
        }

        return await jsonFileStore.LoadAsync<TeamTask>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(TeamTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.UpdatedAt = DateTimeOffset.UtcNow;
        var dir = ResolveTaskDirectory(task.RootSessionId, task.TaskId);
        Directory.CreateDirectory(dir);
        await jsonFileStore.SaveAsync(Path.Combine(dir, "task.json"), task, cancellationToken).ConfigureAwait(false);
        SessionDirectoryLayout.InvalidateNestedIndex(paths.SessionsPath);
    }

    public async Task<IReadOnlyList<TeamTask>> ListForRootSessionAsync(
        string rootSessionId,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(paths.SessionsPath, rootSessionId, "teams");
        if (!Directory.Exists(root))
        {
            return [];
        }

        var results = new List<TeamTask>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(dir, "task.json");
            if (!File.Exists(path))
            {
                continue;
            }

            var task = await jsonFileStore.LoadAsync<TeamTask>(path, cancellationToken).ConfigureAwait(false);
            if (task is not null)
            {
                results.Add(task);
            }
        }

        return results.OrderByDescending(t => t.CreatedAt).ToArray();
    }

    private string GetTaskPath(string rootSessionId, string taskId) =>
        Path.Combine(ResolveTaskDirectory(rootSessionId, taskId), "task.json");
}
