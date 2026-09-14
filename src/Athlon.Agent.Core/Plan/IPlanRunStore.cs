namespace Athlon.Agent.Core.Plan;

/// <summary>
/// Holds the in-flight Plan run for a session. Plans are intentionally ephemeral: nothing is
/// written to disk, so a run only survives for the lifetime of the process and is dropped as
/// soon as the user consumes it by clicking Build.
/// </summary>
public interface IPlanRunStore
{
    Task<PlanRun?> LoadActiveAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SaveActiveAsync(PlanRun run, CancellationToken cancellationToken = default);

    /// <summary>Drops the session's run and its plan markdown.</summary>
    Task ClearActiveAsync(string sessionId, CancellationToken cancellationToken = default);

    Task WritePlanMarkdownAsync(string sessionId, string markdown, CancellationToken cancellationToken = default);

    Task<string?> ReadPlanMarkdownAsync(string sessionId, CancellationToken cancellationToken = default);
}
