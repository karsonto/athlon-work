namespace Athlon.Agent.App.Themes;

/// <summary>
/// Light-theme color tokens from <c>F:\athlon\report\html</c> (Tailwind slate / sky / violet).
/// Sources: <c>src/index.css</c>, <c>App.tsx</c>, <c>AppHeader.tsx</c>, <c>ChatPane.tsx</c>, <c>Sidebar.tsx</c>, <c>Composer.tsx</c>.
/// </summary>
internal static class ReportHtmlLightColors
{
    // Chat gradients (ChatPane / EmptyConversation)
    public const string ChatGradientTop = "#F8FBFF";
    public const string ChatGradientBottom = "#F1F5F9";
    public const string HomeGradientBottom = "#EFF6FF";

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

    // Sky (accent / user bubble / links)
    public const string Sky50 = "#F0F9FF";
    public const string Sky100 = "#E0F2FE";
    public const string Sky200 = "#BAE6FD";
    public const string Sky500 = "#0EA5E9";
    public const string Sky600 = "#0284C7";
    public const string Sky700 = "#0369A1";

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
    public const string Rose600 = "#E11D48";
    public const string Rose700 = "#BE123C";
    public const string Rose50 = "#FFF1F2";
    public const string Amber500 = "#F59E0B";

    // Icon badge (public/athlon-icon.svg)
    public const string IconBadgeGradientStart = "#E0F2FE";
    public const string IconBadgeGradientEnd = "#0284C7";

    // Scrollbar (index.css)
    public const string ScrollThumb = "#94A3B8";
    public const double ScrollThumbOpacity = 0.55;
}
