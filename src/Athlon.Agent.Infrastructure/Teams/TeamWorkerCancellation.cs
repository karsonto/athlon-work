using System.Collections.Concurrent;

namespace Athlon.Agent.Infrastructure.Teams;

/// <summary>Per-subtask CTS registry so Leader <c>team_cancel_subtask</c> can stop a Running Worker.</summary>
internal static class TeamWorkerCancellation
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> ByKey =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Key(string rootSessionId, string taskId, string subTaskId) =>
        $"{rootSessionId}|{taskId}|{subTaskId}";

    public static CancellationTokenSource Register(
        string rootSessionId,
        string taskId,
        string subTaskId,
        CancellationToken parent)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(parent);
        var key = Key(rootSessionId, taskId, subTaskId);
        if (ByKey.TryRemove(key, out var prior))
        {
            try
            {
                prior.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        ByKey[key] = linked;
        return linked;
    }

    public static bool TryCancel(string rootSessionId, string taskId, string subTaskId)
    {
        if (!ByKey.TryGetValue(Key(rootSessionId, taskId, subTaskId), out var cts))
        {
            return false;
        }

        try
        {
            if (!cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public static void Unregister(string rootSessionId, string taskId, string subTaskId)
    {
        if (ByKey.TryRemove(Key(rootSessionId, taskId, subTaskId), out var cts))
        {
            try
            {
                cts.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}
