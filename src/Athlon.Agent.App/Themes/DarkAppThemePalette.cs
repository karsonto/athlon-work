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
        AppBackground = C("#101012"),
        Chrome = C("#18181B"),
        Panel = C("#262628"),
        PanelAlt = C("#2A2A2D"),
        Composer = C("#2A2A2D"),
        Border = C("#3F3F46"),
        Text = C("#F4F4F5"),
        SubtleText = C("#A1A1AA"),
        DisabledText = C("#71717A"),
        DisabledBackground = C("#3F3F46"),
        Accent = C("#2563EB"),
        AccentHover = C("#1D4ED8"),
        UserBubble = C("#1E3A5F"),
        UserBubbleOpacity = 0.86,
        AssistantBubble = C("#262628"),
        Success = C("#10B981"),
        Danger = C("#E11D48"),
        DangerHover = C("#BE123C"),
        Warning = C("#FBBF24"),
        NavActiveBg = C("#1E3A5F"),
        NavActiveText = C("#93C5FD"),
        ToolThinkingBorder = C("#6D28D9"),
        ToolThinkingBg = C("#1E1B2E"),
        ToolThinkingText = C("#DDD6FE"),
        ToolSuccessBorder = C("#059669"),
        ToolSuccessBg = C("#142A22"),
        ToolSuccessText = C("#6EE7B7"),
        ToolFailureBorder = C("#E11D48"),
        ToolFailureBg = C("#2A1418"),
        ToolFailureText = C("#FDA4AF"),
        IconBadgeStart = C("#0284C7"),
        IconBadgeEnd = C("#7DD3FC"),
        HoverNeutral = C("#27272A"),
        HoverNeutralAlt = C("#2F2F34"),
        HoverActive = C("#254766"),
        HoverTool = C("#242237"),
        HoverToolPressed = C("#2C2942"),
        HoverSurface = C("#2A2A2D"),
        HoverSurfacePressed = C("#33333A"),
        SelectionActive = C("#1E3A5F"),
        SelectionInactive = C("#243A55"),
        SelectionBorder = C("#2F5C8E"),
        AtCompletionSkillBadgeBg = C("#1E1B2E"),
        AtCompletionSkillBadgeBorder = C("#6D28D9"),
        AtCompletionSkillBadgeText = C("#DDD6FE"),
        AtCompletionFileBadgeBg = C("#2A2A2D"),
        AtCompletionFileBadgeBorder = C("#3F3F46"),
        AtCompletionFileBadgeText = C("#A1A1AA"),
        CodeBackground = C("#202023"),
        CodeBackgroundAlt = C("#27272A"),
        CodeForeground = C("#F1F5F9"),
        CodeBorder = C("#1E293B"),
        CodeHighlightBlue = C("#93C5FD"),
        TableBorder = C("#52525B"),
        MenuBackground = C("#27272A"),
        MenuHover = C("#3F3F46"),
        ToastBackground = C("#0F172A"),
        ToastBorder = C("#334155"),
        PreviewContentBackground = Colors.White,
        ScrollThumb = C("#9494A8"),
        ScrollThumbOpacity = 0.55,
        ChatBackgroundTop = C("#141416"),
        ChatBackgroundBottom = C("#101012"),
        IconBadgeGradientStart = C("#E0F2FE"),
        IconBadgeGradientEnd = C("#0284C7"),
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
