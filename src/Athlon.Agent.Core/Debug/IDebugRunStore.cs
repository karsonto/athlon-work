namespace Athlon.Agent.Core.Debug;

public interface IDebugRunStore
{
    Task<DebugRun?> LoadActiveAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SaveActiveAsync(DebugRun run, CancellationToken cancellationToken = default);

    Task SaveRunAsync(DebugRun run, CancellationToken cancellationToken = default);

    Task<DebugRun?> LoadRunAsync(string sessionId, string runId, CancellationToken cancellationToken = default);

    Task ClearActiveAsync(string sessionId, CancellationToken cancellationToken = default);

    string CreateLogPath(string runId);
}
