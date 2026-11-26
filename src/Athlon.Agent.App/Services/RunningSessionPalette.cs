using System.Text;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Stable per-session accent colors for concurrent running sessions in the nav sidebar.
/// Uses a deterministic hash so colors stay consistent across process restarts
/// (unlike <see cref="string.GetHashCode"/>, which is randomized per process).
/// </summary>
internal static class RunningSessionPalette
{
    internal static readonly string[] BrushKeys =
    [
        "Brush.Accent",
        "Brush.Success",
        "Brush.ModePlanBorder",
        "Brush.Warning",
        "Brush.ModeAskBorder",
        "Brush.ModeCodingBorder",
        "Brush.Danger",
        "Brush.ModeDebugBorder"
    ];

    public static int GetColorIndex(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return 0;
        }

        return (int)(Fnv1a32(sessionId) % (uint)BrushKeys.Length);
    }

    public static string GetBrushResourceKey(string sessionId) =>
        GetBrushResourceKey(GetColorIndex(sessionId));

    public static string GetBrushResourceKey(int index)
    {
        var length = BrushKeys.Length;
        var normalized = ((index % length) + length) % length;
        return BrushKeys[normalized];
    }

    /// <summary>FNV-1a 32-bit over UTF-8 — stable across processes and runtimes.</summary>
    private static uint Fnv1a32(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }
}
