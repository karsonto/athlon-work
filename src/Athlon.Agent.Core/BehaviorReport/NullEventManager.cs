namespace Athlon.Agent.Core.BehaviorReport;

public sealed class NullEventManager : IEventManager
{
    public static readonly NullEventManager Instance = new();

    public void Record(
        string eventId,
        string eventType,
        string messageContent,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Start()
    {
    }

    public void Stop()
    {
    }
}
