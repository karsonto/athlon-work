namespace Athlon.Agent.Infrastructure;

internal static class FileIoRetry
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(75),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(300)
    ];

    public static Task RunAsync(Func<Task> action, CancellationToken cancellationToken = default) =>
        RunAsync<object?>(async () =>
        {
            await action().ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public static async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= Backoff.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientFileLock(ex) && attempt < Backoff.Length)
            {
                last = ex;
                await Task.Delay(Backoff[attempt], cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("File I/O retry failed without capturing an exception.");
    }

    private static bool IsTransientFileLock(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}
