using System.Windows;
using System.Windows.Media;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.App.Services;

/// <summary>Single entry point for resolving theme brushes and colors at runtime.</summary>
public static class ThemeBrushResolver
{
    public static Brush? TryFindBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;

    public static Color GetColor(string key) => ResolveColor(key);

    public static Brush Get(string key)
    {
        if (TryFindBrush(key) is Brush brush)
        {
            return brush;
        }

        return AppThemeColor.ToFrozenBrush(ResolveColor(key));
    }

    private static Color ResolveColor(string key)
    {
        var chrome = AppThemeManager.Current.Chrome;
        return key switch
        {
            "Brush.AppBackground" => chrome.AppBackground,
            "Brush.Chrome" => chrome.Chrome,
            "Brush.Panel" => chrome.Panel,
            "Brush.PanelAlt" => chrome.PanelAlt,
            "Brush.Composer" => chrome.Composer,
            "Brush.ComposerBorder" => chrome.ComposerBorder,
            "Brush.Border" => chrome.Border,
            "Brush.BorderHover" => chrome.BorderHover,
            "Brush.Text" => chrome.Text,
            "Brush.TextSecondary" => chrome.TextSecondary,
            "Brush.SubtleText" => chrome.SubtleText,
            "Brush.DisabledText" => chrome.DisabledText,
            "Brush.DisabledBackground" => chrome.DisabledBackground,
            "Brush.Accent" => chrome.Accent,
            "Brush.AccentHover" => chrome.AccentHover,
            "Brush.AccentActive" => chrome.AccentActive,
            "Brush.AccentSubtle" => chrome.AccentSubtle,
            "Brush.ModeAgentBg" => chrome.ModeAgentBg,
            "Brush.ModeAgentBorder" => chrome.ModeAgentBorder,
            "Brush.ModeAgentForeground" => chrome.ModeAgentForeground,
            "Brush.ModeCodingBg" => chrome.ModeCodingBg,
            "Brush.ModeCodingBorder" => chrome.ModeCodingBorder,
            "Brush.ModeCodingForeground" => chrome.ModeCodingForeground,
            "Brush.ModeAskBg" => chrome.ModeAskBg,
            "Brush.ModeAskBorder" => chrome.ModeAskBorder,
            "Brush.ModeAskForeground" => chrome.ModeAskForeground,
            "Brush.SurfaceHover" => chrome.SurfaceHover,
            "Brush.SurfaceActive" => chrome.SurfaceActive,
            "Brush.UserBubble" => chrome.UserBubble,
            "Brush.AssistantBubble" => chrome.AssistantBubble,
            "Brush.Success" => chrome.Success,
            "Brush.SuccessSubtle" => chrome.SuccessSubtle,
            "Brush.OnSuccess" => chrome.OnSuccess,
            "Brush.Danger" => chrome.Danger,
            "Brush.DangerHover" => chrome.DangerHover,
            "Brush.ErrorSubtle" => chrome.ErrorSubtle,
            "Brush.Warning" => chrome.Warning,
            "Brush.WarningSubtle" => chrome.WarningSubtle,
            "Brush.NavActiveBg" => chrome.NavActiveBg,
            "Brush.ToolThinkingBorder" => chrome.ToolThinkingBorder,
            "Brush.ToolThinkingBg" => chrome.ToolThinkingBg,
            "Brush.ToolThinkingText" => chrome.ToolThinkingText,
            "Brush.ToolSuccessBorder" => chrome.ToolSuccessBorder,
            "Brush.ToolSuccessBg" => chrome.ToolSuccessBg,
            "Brush.ToolSuccessText" => chrome.ToolSuccessText,
            "Brush.ToolFailureBorder" => chrome.ToolFailureBorder,
            "Brush.ToolFailureBg" => chrome.ToolFailureBg,
            "Brush.ToolFailureText" => chrome.ToolFailureText,
            "Brush.HoverNeutral" => chrome.HoverNeutral,
            "Brush.HoverNeutralAlt" => chrome.HoverNeutralAlt,
            "Brush.HoverActive" => chrome.HoverActive,
            "Brush.HoverTool" => chrome.HoverTool,
            "Brush.HoverToolPressed" => chrome.HoverToolPressed,
            "Brush.HoverSurface" => chrome.HoverSurface,
            "Brush.HoverSurfacePressed" => chrome.HoverSurfacePressed,
            "Brush.SelectionActive" => chrome.SelectionActive,
            "Brush.SelectionInactive" => chrome.SelectionInactive,
            "Brush.SelectionBorder" => chrome.SelectionBorder,
            "Brush.AtCompletionSkillBadgeBg" => chrome.AtCompletionSkillBadgeBg,
            "Brush.AtCompletionSkillBadgeBorder" => chrome.AtCompletionSkillBadgeBorder,
            "Brush.AtCompletionSkillBadgeText" => chrome.AtCompletionSkillBadgeText,
            "Brush.AtCompletionFileBadgeBg" => chrome.AtCompletionFileBadgeBg,
            "Brush.AtCompletionFileBadgeBorder" => chrome.AtCompletionFileBadgeBorder,
            "Brush.AtCompletionFileBadgeText" => chrome.AtCompletionFileBadgeText,
            "Brush.AtCompletionMcpBadgeBg" => chrome.AtCompletionMcpBadgeBg,
            "Brush.AtCompletionMcpBadgeBorder" => chrome.AtCompletionMcpBadgeBorder,
            "Brush.AtCompletionMcpBadgeText" => chrome.AtCompletionMcpBadgeText,
            "Brush.AtCompletionCommandBadgeBg" => chrome.AtCompletionCommandBadgeBg,
            "Brush.AtCompletionCommandBadgeBorder" => chrome.AtCompletionCommandBadgeBorder,
            "Brush.AtCompletionCommandBadgeText" => chrome.AtCompletionCommandBadgeText,
            "Brush.CodeBackground" => chrome.CodeBackground,
            "Brush.CodeBackgroundAlt" => chrome.CodeBackgroundAlt,
            "Brush.CodeForeground" => chrome.CodeForeground,
            "Brush.CodeBorder" => chrome.CodeBorder,
            "Brush.CodeHighlightBlue" => chrome.CodeHighlightBlue,
            "Brush.TableBorder" => chrome.TableBorder,
            "Brush.MenuBackground" => chrome.MenuBackground,
            "Brush.MenuHover" => chrome.MenuHover,
            "Brush.ToastBackground" => chrome.ToastBackground,
            "Brush.ToastBorder" => chrome.ToastBorder,
            "Brush.PreviewContentBackground" => chrome.PreviewContentBackground,
            "Brush.ScrollThumb" => chrome.ScrollThumb,
            _ => chrome.Text,
        };
    }
}
