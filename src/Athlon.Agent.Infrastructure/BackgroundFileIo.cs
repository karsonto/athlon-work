namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Shared off-thread hop for filesystem work that does not justify its own thread pool queue.
///
/// <para>Uses <see cref="TaskScheduler.Default"/> rather than <c>Task.Run</c>: on a dispatcher
/// thread <c>Task.Run</c> inherits the current scheduler and can end up back on the UI thread,
/// which is the exact outcome this exists to prevent. Pinning to the default scheduler guarantees
/// the work lands on the pool, and the caller's continuation still resumes where it was.</para>
///
/// <para>Only for callers already on a thread that must not block. Handing work to the pool from an
/// ordinary background flow buys nothing and costs a scheduling hop.</para>
/// </summary>
internal static class BackgroundFileIo
{
    public static Task RunAsync(Action action) =>
        Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    public static Task<T> RunAsync<T>(Func<T> action) =>
        Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
}
