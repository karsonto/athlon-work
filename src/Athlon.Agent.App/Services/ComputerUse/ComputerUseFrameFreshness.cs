namespace Athlon.Agent.App.Services.ComputerUse;

/// <summary>
/// Shared freshness rules for Computer Use observe → interact handoff.
/// Tuned for LLM latency between observation and the next tool call.
/// </summary>
internal static class ComputerUseFrameFreshness
{
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    internal static bool IsWithinAge(DateTimeOffset createdAt, DateTimeOffset now) =>
        now - createdAt <= MaxAge;

    internal static bool MatchesMonitor(
        int frameLeft,
        int frameTop,
        int frameWidth,
        int frameHeight,
        int currentLeft,
        int currentTop,
        int currentWidth,
        int currentHeight) =>
        frameLeft == currentLeft
        && frameTop == currentTop
        && frameWidth == currentWidth
        && frameHeight == currentHeight;

    internal static bool MatchesForegroundProcess(string? expected, string? actual) =>
        string.Equals(
            expected ?? string.Empty,
            actual ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

    internal static bool ContainsPoint(
        int left,
        int top,
        int width,
        int height,
        int x,
        int y) =>
        x >= left && y >= top && x < left + width && y < top + height;
}
