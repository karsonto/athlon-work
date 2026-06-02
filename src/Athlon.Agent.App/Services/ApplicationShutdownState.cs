namespace Athlon.Agent.App.Services;

public static class ApplicationShutdownState
{
    private static int _completed;

    public static bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public static void MarkCompleted() => Volatile.Write(ref _completed, 1);
}
