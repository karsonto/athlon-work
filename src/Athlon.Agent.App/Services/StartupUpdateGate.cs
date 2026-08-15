using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Previously blocked startup with a MessageBox when an update was available.
/// Updates are now offered via the main-window bottom-left banner after load.
/// </summary>
internal static class StartupUpdateGate
{
    public static void CheckBeforeStartupGates(AppSettings settings)
    {
        // No-op: quiet polling + banner handle updates without blocking startup.
        _ = settings;
    }
}
