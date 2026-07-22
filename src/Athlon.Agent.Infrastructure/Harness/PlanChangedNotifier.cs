using Athlon.Agent.Core.Harness;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class PlanChangedNotifier : IPlanChangedNotifier
{
    public event Action<string>? PlanChanged;

    public void Notify(string sessionId) => PlanChanged?.Invoke(sessionId);
}
