using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Infrastructure.Plan;

public sealed class FilePlanRunStore(IAppPathProvider paths, IJsonFileStore jsonFileStore) : IPlanRunStore
{
    private sealed class ActivePointer
    {
        public string? RunId { get; set; }
    }

    public string GetPlanMarkdownPath(string sessionId)
    {
        var dir = GetSessionPlanDir(sessionId);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "plan.md");
    }

    public async Task WritePlanMarkdownAsync(string sessionId, string markdown, CancellationToken cancellationToken = default)
    {
        var path = GetPlanMarkdownPath(sessionId);
        await File.WriteAllTextAsync(path, markdown ?? string.Empty, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadPlanMarkdownAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = GetPlanMarkdownPath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlanRun?> LoadActiveAsync(string sessionId, CancellationToken cancellationToken = default)
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

    public async Task SaveActiveAsync(PlanRun run, CancellationToken cancellationToken = default)
    {
        await SaveRunAsync(run, cancellationToken).ConfigureAwait(false);
        var pointerPath = GetActivePointerPath(run.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
        await jsonFileStore.SaveAsync(pointerPath, new ActivePointer { RunId = run.Id }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveRunAsync(PlanRun run, CancellationToken cancellationToken = default)
    {
        var path = GetRunPath(run.SessionId, run.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await jsonFileStore.SaveAsync(path, run, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlanRun?> LoadRunAsync(string sessionId, string runId, CancellationToken cancellationToken = default)
    {
        var path = GetRunPath(sessionId, runId);
        return await jsonFileStore.LoadAsync<PlanRun>(path, cancellationToken).ConfigureAwait(false);
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

    public async Task<PlanRun?> LoadApprovedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var active = await LoadActiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (active is not null
            && string.Equals(
                PlanRunStatuses.Normalize(active.Status),
                PlanRunStatuses.Approved,
                StringComparison.OrdinalIgnoreCase))
        {
            return active;
        }

        var runsDir = Path.Combine(paths.RootPath, "plans", "runs", sessionId);
        if (!Directory.Exists(runsDir))
        {
            return null;
        }

        PlanRun? best = null;
        foreach (var file in Directory.EnumerateFiles(runsDir, "*.json"))
        {
            if (string.Equals(Path.GetFileName(file), "active.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var run = await jsonFileStore.LoadAsync<PlanRun>(file, cancellationToken).ConfigureAwait(false);
            if (run is null
                || !string.Equals(
                    PlanRunStatuses.Normalize(run.Status),
                    PlanRunStatuses.Approved,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || run.UpdatedAt > best.UpdatedAt)
            {
                best = run;
            }
        }

        return best;
    }

    private string GetSessionPlanDir(string sessionId) =>
        Path.Combine(paths.RootPath, "plans", "docs", sessionId);

    private string GetRunPath(string sessionId, string runId) =>
        Path.Combine(paths.RootPath, "plans", "runs", sessionId, runId + ".json");

    private string GetActivePointerPath(string sessionId) =>
        Path.Combine(paths.RootPath, "plans", "runs", sessionId, "active.json");
}
