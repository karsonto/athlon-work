namespace Athlon.Agent.Core.Compaction;

public static class TokenCountDisplay
{
    public static string FormatCompact(int value) =>
        value >= 1_000_000 ? $"{value / 1_000_000.0:F1}M"
        : value >= 1_000 ? $"{value / 1_000.0:F1}K"
        : value.ToString();

    /// <summary>
    /// Window sizes that are exact multiples of 1024 display as 128K-style labels.
    /// Other values use <see cref="FormatCompact"/>.
    /// </summary>
    public static string FormatWindow(int value)
    {
        if (value >= 1024 && value % 1024 == 0)
        {
            var kib = value / 1024;
            if (kib >= 1024 && kib % 1024 == 0)
            {
                return $"{kib / 1024}M";
            }

            return $"{kib}K";
        }

        return FormatCompact(value);
    }
}
