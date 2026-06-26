namespace Athlon.Agent.Core.Harness;

public interface ITaskListChangedNotifier
{
    event Action<string>? TaskListChanged;

    void Notify(string sessionId);
}
