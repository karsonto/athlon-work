using System.Collections.Concurrent;
using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class FileSubAgentTaskStore(IAppPathProvider paths, IJsonFileStore jsonFileStore) : ISubAgentTaskStore
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<SubAgentTaskRecord> CreateAsync(
        string parentSessionId,
        string sessionKey,
        string subSessionId,
        CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(parentSessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
            var taskId = $"task_{Guid.NewGuid():N}";
            var record = new SubAgentTaskRecord(
                taskId,
                parentSessionId,
                sessionKey,
                subSessionId,
                "pending",
                null,
                null,
                DateTimeOffset.UtcNow,
                null);
            list.Tasks.Add(record);
            await SaveAsync(parentSessionId, list, cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SubAgentTaskRecord?> GetAsync(
        string parentSessionId,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(parentSessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
            return list.Tasks.FirstOrDefault(task =>
                string.Equals(task.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpdateAsync(
        string parentSessionId,
        SubAgentTaskRecord record,
        CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(parentSessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
            var index = list.Tasks.FindIndex(task =>
                string.Equals(task.TaskId, record.TaskId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                list.Tasks.Add(record);
            }
            else
            {
                list.Tasks[index] = record;
            }

            await SaveAsync(parentSessionId, list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SubAgentTaskListFile> LoadAsync(string parentSessionId, CancellationToken cancellationToken)
    {
        var path = GetPath(parentSessionId);
        var list = await jsonFileStore.LoadAsync<SubAgentTaskListFile>(path, cancellationToken).ConfigureAwait(false);
        return list ?? new SubAgentTaskListFile();
    }

    private Task SaveAsync(string parentSessionId, SubAgentTaskListFile list, CancellationToken cancellationToken)
    {
        var path = GetPath(parentSessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return jsonFileStore.SaveAsync(path, list, cancellationToken);
    }

    private string GetPath(string parentSessionId) =>
        Path.Combine(paths.SessionsPath, parentSessionId, "subagent_tasks.json");
}
