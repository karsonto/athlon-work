namespace Athlon.Agent.App.Services;

/// <summary>
/// Stable per-session accent colors for concurrent running sessions in the nav sidebar.
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

        var hash = unchecked((uint)StringComparer.Ordinal.GetHashCode(sessionId));
        return (int)(hash % (uint)BrushKeys.Length);
    }

    public static string GetBrushResourceKey(string sessionId) =>
        GetBrushResourceKey(GetColorIndex(sessionId));

    public static string GetBrushResourceKey(int index)
    {
        var length = BrushKeys.Length;
        var normalized = ((index % length) + length) % length;
        return BrushKeys[normalized];
    }
}
