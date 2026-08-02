namespace Athlon.Agent.App.Themes;

/// <summary>
/// Light-theme color tokens (Tailwind slate / indigo / violet + Codex-like shell surfaces).
/// Slate scale remains report/html-aligned; <see cref="Sidebar"/> / <see cref="Workspace"/> /
/// <see cref="SoftBorder"/> are the Codex-leaning shell tokens.
/// </summary>
internal static class ReportHtmlLightColors
{
    // Workspace surfaces (Codex-like: white main card on soft grey shell)
    /// <summary>Main chat / settings canvas (white card fill).</summary>
    public const string Workspace = "#FFFFFF";
    /// <summary>Left/right sidebars + shell behind the floating workspace card.</summary>
    public const string Sidebar = "#F5F5F7";
    /// <summary>Softer card borders for floating workspace / composer elevation.</summary>
    public const string SoftBorder = "#E8E8EA";

    // Legacy chat gradient tokens (kept flat for pane unity)
    public const string ChatGradientTop = Workspace;
    public const string ChatGradientBottom = Workspace;

    // Slate (component accents / chips — keep Tailwind values; do not reuse for shell)
    public const string Slate50 = "#F8FAFC";
    public const string Slate100 = "#F1F5F9";
    public const string Slate200 = "#E2E8F0";
    public const string Slate300 = "#CBD5E1";
    public const string Slate400 = "#94A3B8";
    public const string Slate500 = "#64748B";
    public const string Slate600 = "#475569";
    public const string Slate700 = "#334155";
    public const string Slate800 = "#1E293B";
    public const string Slate900 = "#0F172A";

    // Indigo (accent / user bubble / links — aligned with dark theme)
    public const string Indigo50 = "#EEF2FF";
    public const string Indigo100 = "#E0E7FF";
    public const string Indigo200 = "#C7D2FE";
    public const string Indigo500 = "#6366F1";
    public const string Indigo600 = "#4F46E5";
    public const string Indigo700 = "#4338CA";

    // Violet (reasoning / tool-thinking)
    public const string Violet50 = "#F5F3FF";
    public const string Violet100 = "#EDE9FE";
    public const string Violet200 = "#DDD6FE";
    public const string Violet900 = "#4C1D95";

    // Semantic
    public const string White = "#FFFFFF";
    public const string Green600 = "#16A34A";
    public const string Green700 = "#15803D";
    public const string Green50 = "#F0FDF4";
    public const string Green200 = "#BBF7D0";
    public const string Rose600 = "#E11D48";
    public const string Rose700 = "#BE123C";
    public const string Rose50 = "#FFF1F2";
    public const string Rose200 = "#FECDD3";
    public const string Amber500 = "#F59E0B";

    // Scrollbar — Slate500 @ 40% for ~3.2:1 contrast on Slate100
    public const string ScrollThumb = "#64748B";
    public const double ScrollThumbOpacity = 0.40;
}
