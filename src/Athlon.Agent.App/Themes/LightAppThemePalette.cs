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
        // App.tsx: bg-slate-100; body index.css #f1f5f9
        AppBackground = C(ReportHtmlLightColors.Slate100),
        // AppHeader: bg-white border-sky-100
        Chrome = C(ReportHtmlLightColors.White),
        // Assistant bubble / panels: bg-white
        Panel = C(ReportHtmlLightColors.White),
        PanelAlt = C(ReportHtmlLightColors.Slate50),
        // Composer: bg-white/95 border-slate-200
        Composer = C(ReportHtmlLightColors.White),
        ComposerBorder = C(ReportHtmlLightColors.Slate200),
        Border = C(ReportHtmlLightColors.Slate200),
        BorderHover = C(ReportHtmlLightColors.Slate300),
        Text = C(ReportHtmlLightColors.Slate900),
        SubtleText = C(ReportHtmlLightColors.Slate500),
        DisabledText = C(ReportHtmlLightColors.Slate400),
        DisabledBackground = C(ReportHtmlLightColors.Slate200),
        // Buttons: bg-sky-600 hover:bg-sky-700
        Accent = C(ReportHtmlLightColors.Sky600),
        AccentHover = C(ReportHtmlLightColors.Sky700),
        // ChatPane user: bg-sky-600
        UserBubble = C(ReportHtmlLightColors.Sky600),
        UserBubbleOpacity = 1,
        AssistantBubble = C(ReportHtmlLightColors.White),
        Success = C(ReportHtmlLightColors.Green600),
        Danger = C(ReportHtmlLightColors.Rose600),
        DangerHover = C(ReportHtmlLightColors.Rose700),
        Warning = C(ReportHtmlLightColors.Amber500),
        // Sidebar active: bg-sky-50 text-sky-700
        NavActiveBg = C(ReportHtmlLightColors.Sky50),
        NavActiveText = C(ReportHtmlLightColors.Sky700),
        // AssistantReasoningBlock light
        ToolThinkingBorder = C(ReportHtmlLightColors.Violet200),
        ToolThinkingBg = C(ReportHtmlLightColors.Violet50),
        ToolThinkingText = C(ReportHtmlLightColors.Violet900),
        ToolSuccessBorder = C(ReportHtmlLightColors.Green600),
        ToolSuccessBg = C(ReportHtmlLightColors.Green50),
        ToolSuccessText = C(ReportHtmlLightColors.Green700),
        ToolFailureBorder = C(ReportHtmlLightColors.Rose600),
        ToolFailureBg = C(ReportHtmlLightColors.Rose50),
        ToolFailureText = C(ReportHtmlLightColors.Rose700),
        IconBadgeStart = C(ReportHtmlLightColors.Sky600),
        IconBadgeEnd = C(ReportHtmlLightColors.Sky100),
        HoverNeutral = C(ReportHtmlLightColors.Slate50),
        HoverNeutralAlt = C(ReportHtmlLightColors.Slate100),
        HoverActive = C(ReportHtmlLightColors.Sky50),
        HoverTool = C(ReportHtmlLightColors.Violet50),
        HoverToolPressed = C(ReportHtmlLightColors.Violet100),
        HoverSurface = C(ReportHtmlLightColors.Slate50),
        HoverSurfacePressed = C(ReportHtmlLightColors.Slate100),
        // Sidebar history card: border-sky-200 bg-sky-50
        SelectionActive = C(ReportHtmlLightColors.Sky50),
        SelectionInactive = C(ReportHtmlLightColors.Slate50),
        SelectionBorder = C(ReportHtmlLightColors.Sky200),
        AtCompletionSkillBadgeBg = C(ReportHtmlLightColors.Violet50),
        AtCompletionSkillBadgeBorder = C(ReportHtmlLightColors.Violet200),
        AtCompletionSkillBadgeText = C(ReportHtmlLightColors.Violet900),
        AtCompletionFileBadgeBg = C(ReportHtmlLightColors.Slate50),
        AtCompletionFileBadgeBorder = C(ReportHtmlLightColors.Slate200),
        AtCompletionFileBadgeText = C(ReportHtmlLightColors.Slate500),
        // MarkdownContent inline code: bg-slate-200 text-slate-800
        CodeBackground = C(ReportHtmlLightColors.Slate200),
        CodeBackgroundAlt = C(ReportHtmlLightColors.Slate100),
        CodeForeground = C(ReportHtmlLightColors.Slate800),
        CodeBorder = C(ReportHtmlLightColors.Slate200),
        CodeHighlightBlue = C(ReportHtmlLightColors.Sky700),
        TableBorder = C(ReportHtmlLightColors.Slate300),
        MenuBackground = C(ReportHtmlLightColors.White),
        MenuHover = C(ReportHtmlLightColors.Slate50),
        ToastBackground = C(ReportHtmlLightColors.White),
        ToastBorder = C(ReportHtmlLightColors.Slate200),
        PreviewContentBackground = Colors.White,
        ScrollThumb = C(ReportHtmlLightColors.ScrollThumb),
        ScrollThumbOpacity = ReportHtmlLightColors.ScrollThumbOpacity,
        // ChatPane gradient: #f8fbff → #f1f5f9
        ChatBackgroundTop = C(ReportHtmlLightColors.ChatGradientTop),
        ChatBackgroundBottom = C(ReportHtmlLightColors.ChatGradientBottom),
        IconBadgeGradientStart = C(ReportHtmlLightColors.IconBadgeGradientStart),
        IconBadgeGradientEnd = C(ReportHtmlLightColors.IconBadgeGradientEnd),
    };

    private static EditorThemeColors CreateEditor() => new()
    {
        Background = C(ReportHtmlLightColors.White),
        DefaultText = C(ReportHtmlLightColors.Slate900),
        LineNumber = C(ReportHtmlLightColors.Slate500),
        SelectionBackground = C("#ADD6FF"),
        SelectionForeground = C(ReportHtmlLightColors.Slate900),
        CurrentLineBackground = Color.FromArgb(0x33, 0xBA, 0xE6, 0xFD),
        // MarkdownContent links: text-sky-600
        Link = C(ReportHtmlLightColors.Sky600),
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
        Shell = C(ReportHtmlLightColors.Sky600),
        Git = C("#F05032"),
        Yaml = C("#CB171E"),
        Docker = C("#2496ED"),
        Image = C("#A371F7"),
        MsBuild = C("#D97706"),
        Config = C(ReportHtmlLightColors.Slate500),
    };

    private static Color C(string hex) => AppThemeColor.FromHex(hex);
}
