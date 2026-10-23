namespace Athlon.Agent.Core.Storage;

public interface IAttemptEventRecorder
{
    Task AppendAttemptEventAsync(
        string sessionId,
        AgentAttemptEvent entry,
        CancellationToken cancellationToken = default);
}
