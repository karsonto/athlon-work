using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

/// <summary>Light palette aligned with <c>F:\athlon\report\html</c>.</summary>
public static class LightAppThemePalette
{
    public static AppThemePalette Create() => new()
    {
        Kind = AppThemeKind.Light,
        Chrome = CreateChrome(),
        Editor = CreateEditor(),
        FileIcons = CreateFileIcons(),
    };

    private static UiChromeColors CreateChrome() => new()
    {
        // Sidebars + shell: cool grey (Cursor right pane)
        AppBackground = C(ReportHtmlLightColors.Sidebar),
        // Pane headers match their parent surface; chat uses ChatBackground via XAML
        Chrome = C(ReportHtmlLightColors.Workspace),
        // Elevated cards / bubbles
        Panel = C(ReportHtmlLightColors.White),
        PanelAlt = C(ReportHtmlLightColors.Slate50),
        // Composer card
        Composer = C(ReportHtmlLightColors.White),
        ComposerBorder = C(ReportHtmlLightColors.Slate200),
        Border = C(ReportHtmlLightColors.Slate200),
        BorderHover = C(ReportHtmlLightColors.Slate400),
        Text = C(ReportHtmlLightColors.Slate900),
        TextSecondary = C(ReportHtmlLightColors.Slate600),
        SubtleText = C(ReportHtmlLightColors.Slate500),
        DisabledText = C(ReportHtmlLightColors.Slate400),
        DisabledBackground = C(ReportHtmlLightColors.Slate200),
        // Accent — Indigo (aligned with dark theme)
        Accent = C(ReportHtmlLightColors.Indigo500),
        AccentHover = C(ReportHtmlLightColors.Indigo600),
        AccentActive = C(ReportHtmlLightColors.Indigo700),
        AccentSubtle = Ca(0x1F, ReportHtmlLightColors.Indigo500),
        ModeAgentBg = C("#F5F3FF"),
        ModeAgentBorder = C(ReportHtmlLightColors.Indigo500),
        ModeAgentForeground = C(ReportHtmlLightColors.Slate900),
        ModePlanBg = C("#EFF6FF"),
        ModePlanBorder = C("#3B82F6"),
        ModePlanForeground = C(ReportHtmlLightColors.Slate900),
        ModeCodingBg = C("#F7F3EB"),
        ModeCodingBorder = C("#B8956C"),
        ModeCodingForeground = C(ReportHtmlLightColors.Slate900),
        ModeAskBg = C("#F0FDF4"),
        ModeAskBorder = C(ReportHtmlLightColors.Green600),
        ModeAskForeground = C(ReportHtmlLightColors.Slate900),
        SurfaceHover = C(ReportHtmlLightColors.Slate50),
        SurfaceActive = C(ReportHtmlLightColors.Slate100),
        UserBubble = C("#F2F2F2"),
        UserBubbleOpacity = 1,
        AssistantBubble = C(ReportHtmlLightColors.Slate50),
        Success = C(ReportHtmlLightColors.Green600),
        SuccessSubtle = Ca(0x1F, ReportHtmlLightColors.Green600),
        OnSuccess = C("#FFFFFF"),
        Danger = C(ReportHtmlLightColors.Rose600),
        DangerHover = C(ReportHtmlLightColors.Rose700),
        ErrorSubtle = Ca(0x1F, ReportHtmlLightColors.Rose600),
        Warning = C(ReportHtmlLightColors.Amber500),
        WarningSubtle = Ca(0x1F, ReportHtmlLightColors.Amber500),
        NavActiveBg = C(ReportHtmlLightColors.Indigo50),
        ToolThinkingBorder = C(ReportHtmlLightColors.Violet200),
        ToolThinkingBg = C(ReportHtmlLightColors.Violet50),
        ToolThinkingText = C(ReportHtmlLightColors.Slate600),
        ToolSuccessBorder = C(ReportHtmlLightColors.Green200),
        ToolSuccessBg = C(ReportHtmlLightColors.Green50),
        ToolSuccessText = C(ReportHtmlLightColors.Green700),
        ToolFailureBorder = C(ReportHtmlLightColors.Rose200),
        ToolFailureBg = C(ReportHtmlLightColors.Rose50),
        ToolFailureText = C(ReportHtmlLightColors.Rose700),
        HoverNeutral = C(ReportHtmlLightColors.Slate50),
        HoverNeutralAlt = C(ReportHtmlLightColors.Slate100),
        HoverActive = C(ReportHtmlLightColors.Indigo50),
        HoverTool = C(ReportHtmlLightColors.Violet50),
        HoverToolPressed = C(ReportHtmlLightColors.Violet100),
        HoverSurface = C(ReportHtmlLightColors.Slate50),
        HoverSurfacePressed = C(ReportHtmlLightColors.Slate100),
        SelectionActive = C(ReportHtmlLightColors.Indigo50),
        SelectionInactive = C(ReportHtmlLightColors.Slate50),
        SelectionBorder = C(ReportHtmlLightColors.Indigo200),
        AtCompletionSkillBadgeBg = C(ReportHtmlLightColors.Violet50),
        AtCompletionSkillBadgeBorder = C(ReportHtmlLightColors.Violet200),
        AtCompletionSkillBadgeText = C(ReportHtmlLightColors.Violet900),
        AtCompletionFileBadgeBg = C(ReportHtmlLightColors.Slate50),
        AtCompletionFileBadgeBorder = C(ReportHtmlLightColors.Slate200),
        AtCompletionFileBadgeText = C(ReportHtmlLightColors.Slate500),
        AtCompletionMcpBadgeBg = C(ReportHtmlLightColors.Green50),
        AtCompletionMcpBadgeBorder = C(ReportHtmlLightColors.Green200),
        AtCompletionMcpBadgeText = C(ReportHtmlLightColors.Green700),
        AtCompletionCommandBadgeBg = Ca(0x1F, ReportHtmlLightColors.Amber500),
        AtCompletionCommandBadgeBorder = C("#FCD34D"),
        AtCompletionCommandBadgeText = C("#92400E"),
        CodeBackground = C(ReportHtmlLightColors.Slate100),
        CodeBackgroundAlt = C(ReportHtmlLightColors.Slate50),
        CodeForeground = C(ReportHtmlLightColors.Slate800),
        CodeBorder = C(ReportHtmlLightColors.Slate300),
        CodeHighlightBlue = C(ReportHtmlLightColors.Indigo600),
        TableBorder = C(ReportHtmlLightColors.Slate300),
        MenuBackground = C(ReportHtmlLightColors.White),
        MenuHover = C(ReportHtmlLightColors.Slate50),
        ToastBackground = C(ReportHtmlLightColors.White),
        ToastBorder = C(ReportHtmlLightColors.Slate200),
        PreviewContentBackground = Colors.White,
        ScrollThumb = C(ReportHtmlLightColors.ScrollThumb),
        ScrollThumbOpacity = ReportHtmlLightColors.ScrollThumbOpacity,
        // Main workspace: flat off-white (top = bottom)
        ChatBackgroundTop = C(ReportHtmlLightColors.Workspace),
        ChatBackgroundBottom = C(ReportHtmlLightColors.Workspace),
    };

    private static EditorThemeColors CreateEditor() => new()
    {
        Background = C(ReportHtmlLightColors.White),
        DefaultText = C(ReportHtmlLightColors.Slate900),
        LineNumber = C(ReportHtmlLightColors.Slate500),
        SelectionBackground = C("#ADD6FF"),
        SelectionForeground = C(ReportHtmlLightColors.Slate900),
        CurrentLineBackground = Color.FromArgb(0x33, 0xBA, 0xE6, 0xFD),
        Link = C(ReportHtmlLightColors.Indigo600),
        SyntaxTokenColors = EditorSyntaxColorMaps.CreateLight(),
        BoldSyntaxTokenNames = EditorSyntaxColorMaps.BoldTokenNames,
    };

    private static WorkspaceFileIconThemeColors CreateFileIcons() => new()
    {
        Placeholder = C(ReportHtmlLightColors.Slate400),
        Folder = C(ReportHtmlLightColors.Slate500),
        File = C(ReportHtmlLightColors.Slate500),
        CSharp = C("#519ABA"),
        Project = C("#8B5CF6"),
        Solution = C(ReportHtmlLightColors.Slate600),
        Markdown = C("#519ABA"),
        Json = C("#9A8B00"),
        Xml = C("#E37933"),
        Html = C("#E34C26"),
        Css = C("#42A5F5"),
        JavaScript = C("#B58900"),
        TypeScript = C("#3178C6"),
        Python = C("#3572A5"),
        Shell = C(ReportHtmlLightColors.Indigo600),
        Git = C("#F05032"),
        Yaml = C("#CB171E"),
        Docker = C("#2496ED"),
        Image = C("#A371F7"),
        MsBuild = C("#D97706"),
        Config = C(ReportHtmlLightColors.Slate500),
    };

    private static Color C(string hex) => AppThemeColor.FromHex(hex);

    private static Color Ca(byte alpha, string hex)
    {
        var rgb = AppThemeColor.FromHex(hex);
        return Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
    }
}
