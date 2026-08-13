namespace Athlon.Agent.Core.Compaction;

public static class TokenCountDisplay
{
    public static string FormatCompact(int value) =>
        value >= 1_000_000 ? $"{value / 1_000_000.0:F1}M"
        : value >= 1_000 ? $"{value / 1_000.0:F1}K"
        : value.ToString();
}
