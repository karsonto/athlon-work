using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class FileSessionPlanStore(
    IAppPathProvider paths,
    IJsonFileStore jsonFileStore,
    IAgentRunContextAccessor runContextAccessor) : ISessionPlanStore
{
    public async Task<SessionPlan> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new SessionPlan();
        }

        var path = GetPlanFilePath(sessionId);
        var plan = await jsonFileStore.LoadAsync<SessionPlan>(path, cancellationToken).ConfigureAwait(false);
        return plan ?? new SessionPlan();
    }

    public async Task SaveAsync(string sessionId, SessionPlan plan, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        plan.UpdatedAt = DateTime.UtcNow.ToString("O");
        plan.Status = SessionPlanStatuses.Normalize(plan.Status);
        var path = GetPlanFilePath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await jsonFileStore.SaveAsync(path, plan, cancellationToken).ConfigureAwait(false);
    }

    private string GetPlanFilePath(string sessionId)
    {
        var sessionDir = runContextAccessor.ResolveSessionDirectory(paths.SessionsPath, sessionId);
        return Path.Combine(sessionDir, "plan.json");
    }
}
