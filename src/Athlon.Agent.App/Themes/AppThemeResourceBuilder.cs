using System.Windows;
using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

internal static class AppThemeResourceBuilder
{
    private static readonly Uri ControlsUri = new("Themes/Controls.xaml", UriKind.Relative);
    private static readonly Uri OverlaysUri = new("Themes/Overlays.xaml", UriKind.Relative);

    public static ResourceDictionary BuildApplicationResources(AppThemePalette palette)
    {
        var merged = new ResourceDictionary();
        merged.MergedDictionaries.Add(BuildChromeResources(palette.Chrome));

        // Set Source only after merging into the parent tree. Object initializers
        // load XAML immediately, before Add() runs, so StaticResource lookups for
        // Brush.* keys would fail and Foreground would become UnsetValue.
        var controls = new ResourceDictionary();
        merged.MergedDictionaries.Add(controls);
        controls.Source = ControlsUri;

        var overlays = new ResourceDictionary();
        merged.MergedDictionaries.Add(overlays);
        overlays.Source = OverlaysUri;

        return merged;
    }

    public static ResourceDictionary BuildChromeResources(UiChromeColors c)
    {
        var resources = new ResourceDictionary
        {
            ["Brush.AppBackground"] = Brush(c.AppBackground),
            ["Brush.Chrome"] = Brush(c.Chrome),
            ["Brush.Panel"] = Brush(c.Panel),
            ["Brush.PanelAlt"] = Brush(c.PanelAlt),
            ["Brush.Composer"] = Brush(c.Composer),
            ["Brush.Border"] = Brush(c.Border),
            ["Brush.Text"] = Brush(c.Text),
            ["Brush.SubtleText"] = Brush(c.SubtleText),
            ["Brush.DisabledText"] = Brush(c.DisabledText),
            ["Brush.DisabledBackground"] = Brush(c.DisabledBackground),
            ["Brush.Accent"] = Brush(c.Accent),
            ["Brush.AccentHover"] = Brush(c.AccentHover),
            ["Brush.UserBubble"] = Brush(c.UserBubble, c.UserBubbleOpacity),
            ["Brush.AssistantBubble"] = Brush(c.AssistantBubble),
            ["Brush.Success"] = Brush(c.Success),
            ["Brush.Danger"] = Brush(c.Danger),
            ["Brush.DangerHover"] = Brush(c.DangerHover),
            ["Brush.Warning"] = Brush(c.Warning),
            ["Brush.NavActiveBg"] = Brush(c.NavActiveBg),
            ["Brush.NavActiveText"] = Brush(c.NavActiveText),
            ["Brush.ToolThinkingBorder"] = Brush(c.ToolThinkingBorder),
            ["Brush.ToolThinkingBg"] = Brush(c.ToolThinkingBg),
            ["Brush.ToolThinkingText"] = Brush(c.ToolThinkingText),
            ["Brush.ToolSuccessBorder"] = Brush(c.ToolSuccessBorder),
            ["Brush.ToolSuccessBg"] = Brush(c.ToolSuccessBg),
            ["Brush.ToolSuccessText"] = Brush(c.ToolSuccessText),
            ["Brush.ToolFailureBorder"] = Brush(c.ToolFailureBorder),
            ["Brush.ToolFailureBg"] = Brush(c.ToolFailureBg),
            ["Brush.ToolFailureText"] = Brush(c.ToolFailureText),
            ["Brush.IconBadgeStart"] = Brush(c.IconBadgeStart),
            ["Brush.IconBadgeEnd"] = Brush(c.IconBadgeEnd),
            ["Brush.HoverNeutral"] = Brush(c.HoverNeutral),
            ["Brush.HoverNeutralAlt"] = Brush(c.HoverNeutralAlt),
            ["Brush.HoverActive"] = Brush(c.HoverActive),
            ["Brush.HoverTool"] = Brush(c.HoverTool),
            ["Brush.HoverToolPressed"] = Brush(c.HoverToolPressed),
            ["Brush.HoverSurface"] = Brush(c.HoverSurface),
            ["Brush.HoverSurfacePressed"] = Brush(c.HoverSurfacePressed),
            ["Brush.SelectionActive"] = Brush(c.SelectionActive),
            ["Brush.SelectionInactive"] = Brush(c.SelectionInactive),
            ["Brush.SelectionBorder"] = Brush(c.SelectionBorder),
            ["Brush.AtCompletionSkillBadgeBg"] = Brush(c.AtCompletionSkillBadgeBg),
            ["Brush.AtCompletionSkillBadgeBorder"] = Brush(c.AtCompletionSkillBadgeBorder),
            ["Brush.AtCompletionSkillBadgeText"] = Brush(c.AtCompletionSkillBadgeText),
            ["Brush.AtCompletionFileBadgeBg"] = Brush(c.AtCompletionFileBadgeBg),
            ["Brush.AtCompletionFileBadgeBorder"] = Brush(c.AtCompletionFileBadgeBorder),
            ["Brush.AtCompletionFileBadgeText"] = Brush(c.AtCompletionFileBadgeText),
            ["Brush.CodeBackground"] = Brush(c.CodeBackground),
            ["Brush.CodeBackgroundAlt"] = Brush(c.CodeBackgroundAlt),
            ["Brush.CodeForeground"] = Brush(c.CodeForeground),
            ["Brush.CodeBorder"] = Brush(c.CodeBorder),
            ["Brush.CodeHighlightBlue"] = Brush(c.CodeHighlightBlue),
            ["Brush.TableBorder"] = Brush(c.TableBorder),
            ["Brush.MenuBackground"] = Brush(c.MenuBackground),
            ["Brush.MenuHover"] = Brush(c.MenuHover),
            ["Brush.ToastBackground"] = Brush(c.ToastBackground),
            ["Brush.ToastBorder"] = Brush(c.ToastBorder),
            ["Brush.PreviewContentBackground"] = Brush(c.PreviewContentBackground),
            ["Brush.ScrollThumb"] = Brush(c.ScrollThumb, c.ScrollThumbOpacity),
            ["Brush.ChatBackground"] = ChatGradient(c),
            ["Brush.IconBadge"] = IconBadgeGradient(c),
        };
        return resources;
    }

    private static LinearGradientBrush ChatGradient(UiChromeColors c)
    {
        var brush = new LinearGradientBrush(c.ChatBackgroundTop, c.ChatBackgroundBottom, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush IconBadgeGradient(UiChromeColors c)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(c.IconBadgeGradientStart, 0));
        brush.GradientStops.Add(new GradientStop(c.IconBadgeGradientEnd, 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Brush(Color color, double? opacity = null) =>
        AppThemeColor.ToFrozenBrush(color, opacity);
}
