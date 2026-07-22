using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

public static class DarkAppThemePalette
{
    public static AppThemePalette Create() => new()
    {
        Kind = AppThemeKind.Dark,
        Chrome = CreateChrome(),
        Editor = CreateEditor(),
        FileIcons = CreateFileIcons(),
    };

    private static UiChromeColors CreateChrome() => new()
    {
        // Base — Calm Intelligence dark theme
        AppBackground = C("#0A0A0C"),
        Chrome = C("#121214"),
        Panel = C("#1A1A1E"),
        PanelAlt = C("#222226"),
        Composer = C("#1C1C1E"),
        ComposerBorder = C("#2C2C30"),

        // Borders
        Border = C("#27272A"),
        BorderHover = C("#3F3F46"),

        // Text — high contrast with improved readability
        Text = C("#F9FAFB"),
        TextSecondary = C("#D1D5DB"),
        SubtleText = C("#A1A1AA"),
        DisabledText = C("#71717A"),
        DisabledBackground = C("#323236"),

        // Accent — Indigo for better distinction
        Accent = C("#6366F1"),
        AccentHover = C("#4F46E5"),
        AccentActive = C("#4338CA"),
        AccentSubtle = Ca(0x26, "#6366F1"),
        ModeAgentBg = C("#1A1A22"),
        ModeAgentBorder = C("#818CF8"),
        ModeAgentForeground = C("#F4F4F5"),
        ModePlanBg = C("#141A24"),
        ModePlanBorder = C("#60A5FA"),
        ModePlanForeground = C("#F4F4F5"),
        ModeCodingBg = C("#1C1A16"),
        ModeCodingBorder = C("#C4A574"),
        ModeCodingForeground = C("#F4F4F5"),
        ModeAskBg = C("#141A17"),
        ModeAskBorder = C("#34D399"),
        ModeAskForeground = C("#F4F4F5"),
        SurfaceHover = C("#1A1A1E"),
        SurfaceActive = C("#222226"),

        // Chat bubbles
        UserBubble = C("#3A3A3C"),
        UserBubbleOpacity = 1,
        AssistantBubble = C("#121214"),

        // Semantic
        Success = C("#10B981"),
        SuccessSubtle = Ca(0x26, "#10B981"),
        OnSuccess = C("#FFFFFF"),
        Danger = C("#EF4444"),
        DangerHover = C("#DC2626"),
        ErrorSubtle = Ca(0x26, "#EF4444"),
        Warning = C("#F59E0B"),
        WarningSubtle = Ca(0x26, "#F59E0B"),

        // Navigation — accent subtle for active session
        NavActiveBg = Ca(0x26, "#6366F1"),

        // Tool call cards — neutral elevated surfaces
        ToolThinkingBorder = C("#27272A"),
        ToolThinkingBg = C("#121214"),
        ToolThinkingText = C("#D1D5DB"),
        ToolSuccessBorder = C("#27272A"),
        ToolSuccessBg = C("#121214"),
        ToolSuccessText = C("#10B981"),
        ToolFailureBorder = C("#27272A"),
        ToolFailureBg = C("#121214"),
        ToolFailureText = C("#EF4444"),

        // Hover states — more distinct for better feedback
        HoverNeutral = C("#1A1A1E"),
        HoverNeutralAlt = C("#222226"),
        HoverActive = C("#254766"),
        HoverTool = C("#242237"),
        HoverToolPressed = C("#2C2942"),
        HoverSurface = C("#28282B"),
        HoverSurfacePressed = C("#303034"),

        // Selection
        SelectionActive = C("#1E3A5F"),
        SelectionInactive = C("#1F2D45"),
        SelectionBorder = C("#2F5C8E"),

        // Completion popup badges
        AtCompletionSkillBadgeBg = C("#1C1A2E"),
        AtCompletionSkillBadgeBorder = C("#5B21B6"),
        AtCompletionSkillBadgeText = C("#C4B5FD"),
        AtCompletionFileBadgeBg = C("#262628"),
        AtCompletionFileBadgeBorder = C("#323236"),
        AtCompletionFileBadgeText = C("#9CA3AF"),
        AtCompletionMcpBadgeBg = C("#14231F"),
        AtCompletionMcpBadgeBorder = C("#065F46"),
        AtCompletionMcpBadgeText = C("#6EE7B7"),
        AtCompletionCommandBadgeBg = Ca(0x26, "#F59E0B"),
        AtCompletionCommandBadgeBorder = C("#92400E"),
        AtCompletionCommandBadgeText = C("#FCD34D"),

        // Code blocks
        CodeBackground = C("#18181B"),
        CodeBackgroundAlt = C("#202023"),
        CodeForeground = C("#F1F5F9"),
        CodeBorder = C("#1E293B"),
        CodeHighlightBlue = C("#60A5FA"),
        TableBorder = C("#404048"),

        // Menus
        MenuBackground = C("#202022"),
        MenuHover = C("#323236"),

        // Toast
        ToastBackground = C("#0F172A"),
        ToastBorder = C("#2D3A5A"),

        // Preview
        PreviewContentBackground = Colors.White,

        // Scroll
        ScrollThumb = C("#8888A0"),
        ScrollThumbOpacity = 0.50,

        // Chat — same flat surface as Chrome (no vertical gradient strip)
        ChatBackgroundTop = C("#121214"),
        ChatBackgroundBottom = C("#121214"),
    };

    private static EditorThemeColors CreateEditor() => new()
    {
        Background = C("#1E1E1E"),
        DefaultText = C("#D4D4D4"),
        LineNumber = C("#858585"),
        SelectionBackground = C("#264F78"),
        SelectionForeground = Colors.White,
        CurrentLineBackground = Color.FromArgb(0x33, 0x26, 0x4F, 0x78),
        Link = C("#4FC1FF"),
        SyntaxTokenColors = EditorSyntaxColorMaps.CreateDark(),
        BoldSyntaxTokenNames = EditorSyntaxColorMaps.BoldTokenNames,
    };

    private static WorkspaceFileIconThemeColors CreateFileIcons() => new()
    {
        Placeholder = C("#6B7280"),
        Folder = C("#C5C5C5"),
        File = C("#C5C5C5"),
        CSharp = C("#519ABA"),
        Project = C("#8B5CF6"),
        Solution = C("#D4D4D4"),
        Markdown = C("#519ABA"),
        Json = C("#CBCB41"),
        Xml = C("#E37933"),
        Html = C("#E34C26"),
        Css = C("#42A5F5"),
        JavaScript = C("#F0DB4F"),
        TypeScript = C("#3178C6"),
        Python = C("#3572A5"),
        Shell = C("#4FC1FF"),
        Git = C("#F05032"),
        Yaml = C("#CB171E"),
        Docker = C("#2496ED"),
        Image = C("#A371F7"),
        MsBuild = C("#F5B041"),
        Config = C("#6B7280"),
    };

    private static Color C(string hex) => AppThemeColor.FromHex(hex);

    private static Color Ca(byte alpha, string hex)
    {
        var rgb = AppThemeColor.FromHex(hex);
        return Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
    }
}
