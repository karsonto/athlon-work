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
        // Base — deep neutral with subtle warmth instead of pure gray
        AppBackground = C("#0C0C0E"),
        Chrome = C("#161618"),
        Panel = C("#202022"),
        PanelAlt = C("#262628"),
        Composer = C("#1C1C1E"),
        ComposerBorder = C("#2C2C30"),

        // Borders — softer than before
        Border = C("#323236"),

        // Text — high contrast
        Text = C("#F4F4F5"),
        SubtleText = C("#9CA3AF"),
        DisabledText = C("#6B7280"),
        DisabledBackground = C("#323236"),

        // Accent — more vibrant blue
        Accent = C("#3B82F6"),
        AccentHover = C("#2563EB"),

        // Chat bubbles
        UserBubble = C("#1E3A5F"),
        UserBubbleOpacity = 0.88,
        AssistantBubble = C("#202022"),

        // Semantic
        Success = C("#10B981"),
        Danger = C("#EF4444"),
        DangerHover = C("#DC2626"),
        Warning = C("#F59E0B"),

        // Navigation
        NavActiveBg = C("#1E3A5F"),
        NavActiveText = C("#93C5FD"),

        // Tool call cards — refined purple tones
        ToolThinkingBorder = C("#5B21B6"),
        ToolThinkingBg = C("#1C1A2E"),
        ToolThinkingText = C("#C4B5FD"),
        ToolSuccessBorder = C("#059669"),
        ToolSuccessBg = C("#142A22"),
        ToolSuccessText = C("#6EE7B7"),
        ToolFailureBorder = C("#DC2626"),
        ToolFailureBg = C("#2A1418"),
        ToolFailureText = C("#FDA4AF"),

        // Icon badges
        IconBadgeStart = C("#0284C7"),
        IconBadgeEnd = C("#7DD3FC"),

        // Hover states — more distinct
        HoverNeutral = C("#27272A"),
        HoverNeutralAlt = C("#2D2D31"),
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

        // Chat gradient — warmer deep tone
        ChatBackgroundTop = C("#111114"),
        ChatBackgroundBottom = C("#0C0C0E"),

        // Icon badge gradient
        IconBadgeGradientStart = C("#DBEAFE"),
        IconBadgeGradientEnd = C("#3B82F6"),
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
}
