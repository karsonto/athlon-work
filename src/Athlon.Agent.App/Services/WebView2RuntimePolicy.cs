namespace Athlon.Agent.App.Services;

/// <summary>Chooses whether the app should use the bundled Fixed Version WebView2 runtime.</summary>
internal static class WebView2RuntimePolicy
{
    // Windows 11 starts at build 22000; it ships Evergreen WebView2 by default.
    private const int Windows11FirstBuild = 22000;

    public static bool ShouldUseBundledRuntime() =>
        OperatingSystem.IsWindows()
        && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, Windows11FirstBuild);

    internal static bool ShouldUseBundledRuntime(int osBuild) =>
        osBuild < Windows11FirstBuild;
}
