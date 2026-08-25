namespace Athlon.Agent.Core.Plan;

public interface IPlanRunStore
{
    Task<PlanRun?> LoadActiveAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SaveActiveAsync(PlanRun run, CancellationToken cancellationToken = default);

    Task SaveRunAsync(PlanRun run, CancellationToken cancellationToken = default);

    Task<PlanRun?> LoadRunAsync(string sessionId, string runId, CancellationToken cancellationToken = default);

    Task ClearActiveAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Absolute path for the human-readable plan markdown for a session.</summary>
    string GetPlanMarkdownPath(string sessionId);

    Task WritePlanMarkdownAsync(string sessionId, string markdown, CancellationToken cancellationToken = default);

    Task<string?> ReadPlanMarkdownAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<PlanRun?> LoadApprovedAsync(string sessionId, CancellationToken cancellationToken = default);
}
