namespace Athlon.Agent.Core.Teams;

/// <summary>Per-task mutual exclusion for task.json read-modify-write under parallel workers.</summary>
public static class TeamTaskLock
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IDisposable> AcquireAsync(string rootSessionId, string taskId, CancellationToken cancellationToken = default)
    {
        var key = rootSessionId + "\n" + taskId;
        var gate = Locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            var g = Interlocked.Exchange(ref _gate, null);
            g?.Release();
        }
    }
}
