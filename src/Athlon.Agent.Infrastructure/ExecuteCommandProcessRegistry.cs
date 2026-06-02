using System.Collections.Concurrent;
using System.Diagnostics;

namespace Athlon.Agent.Infrastructure;

public sealed class ExecuteCommandProcessRegistry
{
    private readonly ConcurrentDictionary<int, Process> _processes = new();

    public void Register(Process process) => _processes[process.Id] = process;

    public void Unregister(Process process) => _processes.TryRemove(process.Id, out _);

    public void KillAll()
    {
        foreach (var process in _processes.Values.ToArray())
        {
            ProcessKillHelper.KillProcessTree(process);
            _processes.TryRemove(process.Id, out _);
        }
    }
}
