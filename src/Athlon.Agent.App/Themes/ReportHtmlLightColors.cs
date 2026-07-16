namespace Athlon.Agent.App.Themes;

/// <summary>
/// Light-theme color tokens from <c>F:\athlon\report\html</c> (Tailwind slate / indigo / violet).
/// Sources: <c>src/index.css</c>, <c>App.tsx</c>, <c>AppHeader.tsx</c>, <c>ChatPane.tsx</c>, <c>Sidebar.tsx</c>, <c>Composer.tsx</c>.
/// </summary>
internal static class ReportHtmlLightColors
{
    // Workspace surfaces (Cursor-style flat panes)
    /// <summary>Main chat / editor canvas — slightly lighter than sidebars.</summary>
    public const string Workspace = "#F8FAFC";
    /// <summary>Left/right sidebars — cool grey, slightly deeper than workspace.</summary>
    public const string Sidebar = "#F1F5F9";

    // Legacy chat gradient tokens (kept flat for pane unity)
    public const string ChatGradientTop = Workspace;
    public const string ChatGradientBottom = Workspace;

    // Slate
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
