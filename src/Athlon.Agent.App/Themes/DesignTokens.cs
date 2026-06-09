namespace Athlon.Agent.App.Themes;

/// <summary>
/// Design tokens for consistent spacing, radius, and animation durations.
/// Based on the "Calm Intelligence" design system.
/// </summary>
public static class DesignTokens
{
    /// <summary>
    /// Spacing scale based on 4px multiples.
    /// </summary>
    public static class Spacing
    {
        public const double Xs = 4;      // Extra small spacing
        public const double Sm = 8;      // Small spacing
        public const double Md = 12;     // Medium spacing
        public const double Lg = 16;     // Large spacing
        public const double Xl = 20;     // Extra large spacing
        public const double Xxl = 24;    // Extra extra large spacing
        public const double Xxxl = 32;   // Triple extra large spacing
        public const double Huge = 40;   // Huge spacing
        public const double Xhuge = 48;  // Extra huge spacing
    }

    /// <summary>
    /// Border radius scale.
    /// </summary>
    public static class Radius
    {
        public const double Xs = 4;      // Extra small radius
        public const double Sm = 6;      // Small radius
        public const double Md = 8;      // Medium radius
        public const double Lg = 12;     // Large radius
        public const double Xl = 16;     // Extra large radius
        public const double Full = 9999; // Full radius (circle/pill)
    }

    /// <summary>
    /// Animation duration constants.
    /// </summary>
    public static class Duration
    {
        public const double Instant = 75;   // 75ms - Instant feedback (hover states)
        public const double Fast = 150;     // 150ms - Fast transitions (panel expansions)
        public const double Normal = 200;   // 200ms - Normal transitions (page switches)
        public const double Slow = 240;     // 240ms - Slow transitions (complex animations)
    }

    /// <summary>
    /// Easing functions as strings for Storyboard usage.
    /// </summary>
    public static class Easing
    {
        /// <summary>cubic-bezier(0.4, 0, 0.2, 1) - Standard ease-out</summary>
        public const string EaseOut = "0.4,0,0.2,1";
        
        /// <summary>cubic-bezier(0.4, 0, 0.6, 1) - Standard ease-in-out</summary>
        public const string EaseInOut = "0.4,0,0.6,1";
    }
}
