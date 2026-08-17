using Athlon.Agent.Core.Debug;

namespace Athlon.Agent.Infrastructure.Debug;

public sealed class FileDebugRunStore(IAppPathProvider paths, IJsonFileStore jsonFileStore) : IDebugRunStore
{
    private sealed class ActivePointer
    {
        public string? RunId { get; set; }
    }

    public string CreateLogPath(string runId)
    {
        var logsDir = Path.Combine(paths.RootPath, "debug", "logs");
        Directory.CreateDirectory(logsDir);
        var filePath = Path.Combine(logsDir, runId + ".jsonl");
        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, string.Empty);
        }

        return filePath;
    }

    public async Task<DebugRun?> LoadActiveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var pointerPath = GetActivePointerPath(sessionId);
        var pointer = await jsonFileStore.LoadAsync<ActivePointer>(pointerPath, cancellationToken).ConfigureAwait(false);
        if (pointer?.RunId is null)
        {
            return null;
        }

        return await LoadRunAsync(sessionId, pointer.RunId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveActiveAsync(DebugRun run, CancellationToken cancellationToken = default)
    {
        await SaveRunAsync(run, cancellationToken).ConfigureAwait(false);
        var pointerPath = GetActivePointerPath(run.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
        await jsonFileStore.SaveAsync(pointerPath, new ActivePointer { RunId = run.Id }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveRunAsync(DebugRun run, CancellationToken cancellationToken = default)
    {
        var path = GetRunPath(run.SessionId, run.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await jsonFileStore.SaveAsync(path, run, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DebugRun?> LoadRunAsync(string sessionId, string runId, CancellationToken cancellationToken = default)
    {
        var path = GetRunPath(sessionId, runId);
        return await jsonFileStore.LoadAsync<DebugRun>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearActiveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var pointerPath = GetActivePointerPath(sessionId);
        if (File.Exists(pointerPath))
        {
            File.Delete(pointerPath);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private string GetRunPath(string sessionId, string runId) =>
        Path.Combine(paths.RootPath, "debug", "runs", sessionId, runId + ".json");

    private string GetActivePointerPath(string sessionId) =>
        Path.Combine(paths.RootPath, "debug", "runs", sessionId, "active.json");
}
