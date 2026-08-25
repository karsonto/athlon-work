namespace Athlon.Agent.Core.Threading;

/// <summary>
/// Runs async work from synchronous call sites without capturing the ambient
/// <see cref="SynchronizationContext"/> (e.g. WPF dispatcher). Direct
/// <c>GetAwaiter().GetResult()</c> on async I/O that posts continuations back to
/// the UI thread deadlocks the dispatcher — contributors hit this during
/// <c>BuildRuntimeContext</c> after Plan→Coding.
/// </summary>
public static class SyncOverAsync
{
    public static T Run<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Task.Run(action).GetAwaiter().GetResult();
    }

    public static void Run(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Task.Run(action).GetAwaiter().GetResult();
    }
}
