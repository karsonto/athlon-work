using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

/// <summary>Application chrome colors (panels, text, accents). Keys map to <c>Brush.*</c> resources.</summary>
public sealed class UiChromeColors
{
    public required Color AppBackground { get; init; }
    public required Color Chrome { get; init; }
    public required Color Panel { get; init; }
    public required Color PanelAlt { get; init; }
    public required Color Composer { get; init; }
    public required Color ComposerBorder { get; init; }
    public required Color Border { get; init; }
    public required Color BorderHover { get; init; }
    public required Color Text { get; init; }
    public required Color TextSecondary { get; init; }
    public required Color SubtleText { get; init; }
    public required Color DisabledText { get; init; }
    public required Color DisabledBackground { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentActive { get; init; }
    public required Color AccentSubtle { get; init; }
    public required Color SurfaceHover { get; init; }
    public required Color SurfaceActive { get; init; }
    public required Color UserBubble { get; init; }
    public required double UserBubbleOpacity { get; init; }
    public required Color AssistantBubble { get; init; }
    public required Color Success { get; init; }
    public required Color SuccessSubtle { get; init; }
    public required Color Danger { get; init; }
    public required Color DangerHover { get; init; }
    public required Color ErrorSubtle { get; init; }
    public required Color Warning { get; init; }
    public required Color WarningSubtle { get; init; }
    public required Color NavActiveBg { get; init; }
    public required Color ToolThinkingBorder { get; init; }
    public required Color ToolThinkingBg { get; init; }
    public required Color ToolThinkingText { get; init; }
    public required Color ToolSuccessBorder { get; init; }
    public required Color ToolSuccessBg { get; init; }
    public required Color ToolSuccessText { get; init; }
    public required Color ToolFailureBorder { get; init; }
    public required Color ToolFailureBg { get; init; }
    public required Color ToolFailureText { get; init; }
    public required Color HoverNeutral { get; init; }
    public required Color HoverNeutralAlt { get; init; }
    public required Color HoverActive { get; init; }
    public required Color HoverTool { get; init; }
    public required Color HoverToolPressed { get; init; }
    public required Color HoverSurface { get; init; }
    public required Color HoverSurfacePressed { get; init; }
    public required Color SelectionActive { get; init; }
    public required Color SelectionInactive { get; init; }
    public required Color SelectionBorder { get; init; }
    public required Color AtCompletionSkillBadgeBg { get; init; }
    public required Color AtCompletionSkillBadgeBorder { get; init; }
    public required Color AtCompletionSkillBadgeText { get; init; }
    public required Color AtCompletionFileBadgeBg { get; init; }
    public required Color AtCompletionFileBadgeBorder { get; init; }
    public required Color AtCompletionFileBadgeText { get; init; }
    public required Color CodeBackground { get; init; }
    public required Color CodeBackgroundAlt { get; init; }
    public required Color CodeForeground { get; init; }
    public required Color CodeBorder { get; init; }
    public required Color CodeHighlightBlue { get; init; }
    public required Color TableBorder { get; init; }
    public required Color MenuBackground { get; init; }
    public required Color MenuHover { get; init; }
    public required Color ToastBackground { get; init; }
    public required Color ToastBorder { get; init; }
    public required Color PreviewContentBackground { get; init; }
    public required Color ScrollThumb { get; init; }
    public required double ScrollThumbOpacity { get; init; }
    public required Color ChatBackgroundTop { get; init; }
    public required Color ChatBackgroundBottom { get; init; }
}
