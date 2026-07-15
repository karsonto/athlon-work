namespace Athlon.Agent.Core.BehaviorReport;

public interface IEventManager
{
    void Record(
        string eventId,
        string eventType,
        string messageContent,
        IReadOnlyDictionary<string, object?>? parameters = null);

    Task FlushAsync(CancellationToken cancellationToken = default);

    void Start();

    void Stop();
}
