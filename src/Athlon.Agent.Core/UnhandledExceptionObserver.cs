namespace Athlon.Agent.Core;

/// <summary>
/// Process-wide exception observation sink. Writes through a pluggable callback so
/// WPF wiring and unit tests share one format. Does not mark exceptions handled —
/// local boundaries remain responsible for recovery.
/// </summary>
public static class UnhandledExceptionObserver
{
    private static readonly object Gate = new();
    private static Action<string, Exception?>? _sink;
    private static int _registered;

    /// <summary>
    /// Installs the log sink used by process hooks. Safe to call before DI is ready.
    /// </summary>
    public static void Configure(Action<string, Exception?> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Gate)
        {
            _sink = sink;
        }
    }

    public static void Report(string source, Exception? exception)
    {
        Action<string, Exception?>? sink;
        lock (Gate)
        {
            sink = _sink;
        }

        var message = exception is null
            ? $"Unhandled exception ({source})"
            : $"Unhandled exception ({source}): {exception}";

        try
        {
            sink?.Invoke(message, exception);
        }
        catch
        {
            // Never throw from an unhandled-exception path.
        }
    }

    /// <summary>
    /// Registers <see cref="AppDomain.UnhandledException"/> and
    /// <see cref="TaskScheduler.UnobservedTaskException"/> once per process.
    /// Dispatcher wiring stays in the WPF App host.
    /// </summary>
    public static void RegisterProcessHooks()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Report("AppDomain", args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Report("UnobservedTask", args.Exception);
            // Observed so the process is not torn down solely for logging purposes.
            args.SetObserved();
        };
    }

    /// <summary>Test-only: clears the log sink. Process hooks stay registered.</summary>
    internal static void ResetSinkForTests()
    {
        lock (Gate)
        {
            _sink = null;
        }
    }
}
