using System.Runtime.InteropServices;

namespace Athlon.Agent.App.Services;

internal static class SystemKeepAwakeHelper
{
    private const uint EsContinuous = 0x8000_0000;
    private const uint EsSystemRequired = 0x0000_0001;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private static int _referenceCount;

    public static void Acquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (Interlocked.Increment(ref _referenceCount) == 1)
        {
            SetThreadExecutionState(EsContinuous | EsSystemRequired);
        }
    }

    public static void Release()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (Interlocked.Decrement(ref _referenceCount) <= 0)
        {
            Interlocked.Exchange(ref _referenceCount, 0);
            SetThreadExecutionState(EsContinuous);
        }
    }
}
