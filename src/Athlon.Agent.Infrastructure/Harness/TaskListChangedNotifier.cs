using Athlon.Agent.Core.Harness;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class TaskListChangedNotifier : ITaskListChangedNotifier
{
    public event Action<string>? TaskListChanged;

    public void Notify(string sessionId) => TaskListChanged?.Invoke(sessionId);
}
