using Athlon.Agent.Core;
using Athlon.Agent.Core.Storage;
using Athlon.Agent.Infrastructure.BehaviorReport;

namespace Athlon.Agent.Infrastructure.Storage.Repositories;

public sealed class AttemptEventRecorder : IAttemptEventRecorder
{
    public Task AppendAttemptEventAsync(
        string sessionId,
        AgentAttemptEvent entry,
        CancellationToken cancellationToken = default)
    {
        try
        {
            BehaviorEventManager.Instance.RecordAttempt(entry);
        }
        catch
        {
            // Behavior reporting must never affect persistence.
        }

        return Task.CompletedTask;
    }
}
