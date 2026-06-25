using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class FileSessionTaskListStore(
    IAppPathProvider paths,
    IJsonFileStore jsonFileStore,
    IAgentRunContextAccessor runContextAccessor) : ISessionTaskListStore
{
    public async Task<SessionTaskList> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new SessionTaskList();
        }

        var path = GetTasksFilePath(sessionId);
        var list = await jsonFileStore.LoadAsync<SessionTaskList>(path, cancellationToken).ConfigureAwait(false);
        return list ?? new SessionTaskList();
    }

    public async Task ReplaceAsync(string sessionId, SessionTaskList list, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var path = GetTasksFilePath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await jsonFileStore.SaveAsync(path, list, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionTaskList> ApplyMergeAsync(
        string sessionId,
        IReadOnlyList<AgentTaskItem> todos,
        bool merge,
        CancellationToken cancellationToken = default)
    {
        SessionTaskList list;
        if (merge)
        {
            list = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var byId = list.Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var todo in todos)
            {
                byId[todo.Id] = todo;
            }

            list.Items = byId.Values.ToList();
        }
        else
        {
            list = new SessionTaskList { Items = todos.ToList() };
        }

        await ReplaceAsync(sessionId, list, cancellationToken).ConfigureAwait(false);
        return list;
    }

    private string GetTasksFilePath(string sessionId)
    {
        var sessionDir = runContextAccessor.ResolveSessionDirectory(paths.SessionsPath, sessionId);
        return Path.Combine(sessionDir, "tasks.json");
    }
}
