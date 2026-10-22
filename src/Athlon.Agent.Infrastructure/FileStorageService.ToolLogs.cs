using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.BehaviorReport;

namespace Athlon.Agent.Infrastructure;

/// <summary>Attempt-event forwarding (behavior report). Tool-call disk logs have been removed.</summary>
public sealed partial class FileStorageService
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
