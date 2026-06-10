using System.Windows;
using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

internal static class AppThemeResourceBuilder
{
    internal const string TextBrushKey = "Brush.Text";

    public static ResourceDictionary BuildChromeResources(UiChromeColors c)
    {
        var resources = new ResourceDictionary
        {
            ["Brush.AppBackground"] = Brush(c.AppBackground),
            ["Brush.Chrome"] = Brush(c.Chrome),
            ["Brush.Panel"] = Brush(c.Panel),
            ["Brush.PanelAlt"] = Brush(c.PanelAlt),
            ["Brush.Composer"] = Brush(c.Composer),
            ["Brush.ComposerBorder"] = Brush(c.ComposerBorder),
            ["Brush.Border"] = Brush(c.Border),
            ["Brush.BorderHover"] = Brush(c.BorderHover),
            [TextBrushKey] = Brush(c.Text),
            ["Brush.TextSecondary"] = Brush(c.TextSecondary),
            ["Brush.SubtleText"] = Brush(c.SubtleText),
            ["Brush.DisabledText"] = Brush(c.DisabledText),
            ["Brush.DisabledBackground"] = Brush(c.DisabledBackground),
            ["Brush.Accent"] = Brush(c.Accent),
            ["Brush.AccentHover"] = Brush(c.AccentHover),
            ["Brush.AccentActive"] = Brush(c.AccentActive),
            ["Brush.AccentSubtle"] = Brush(c.AccentSubtle),
            ["Brush.SurfaceHover"] = Brush(c.SurfaceHover),
            ["Brush.SurfaceActive"] = Brush(c.SurfaceActive),
            ["Brush.UserBubble"] = Brush(c.UserBubble, c.UserBubbleOpacity),
            ["Brush.AssistantBubble"] = Brush(c.AssistantBubble),
            ["Brush.Success"] = Brush(c.Success),
            ["Brush.SuccessSubtle"] = Brush(c.SuccessSubtle),
            ["Brush.Danger"] = Brush(c.Danger),
            ["Brush.DangerHover"] = Brush(c.DangerHover),
            ["Brush.ErrorSubtle"] = Brush(c.ErrorSubtle),
            ["Brush.Warning"] = Brush(c.Warning),
            ["Brush.WarningSubtle"] = Brush(c.WarningSubtle),
            ["Brush.NavActiveBg"] = Brush(c.NavActiveBg),
            ["Brush.ToolThinkingBorder"] = Brush(c.ToolThinkingBorder),
            ["Brush.ToolThinkingBg"] = Brush(c.ToolThinkingBg),
            ["Brush.ToolThinkingText"] = Brush(c.ToolThinkingText),
            ["Brush.ToolSuccessBorder"] = Brush(c.ToolSuccessBorder),
            ["Brush.ToolSuccessBg"] = Brush(c.ToolSuccessBg),
            ["Brush.ToolSuccessText"] = Brush(c.ToolSuccessText),
            ["Brush.ToolFailureBorder"] = Brush(c.ToolFailureBorder),
            ["Brush.ToolFailureBg"] = Brush(c.ToolFailureBg),
            ["Brush.ToolFailureText"] = Brush(c.ToolFailureText),
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
        };
        return resources;
    }

    public static void ApplyPalette(ResourceDictionary root, UiChromeColors chrome)
    {
        var palette = FindPaletteDictionary(root) ?? InsertPaletteDictionary(root);
        CopyPaletteEntries(palette, BuildChromeResources(chrome));
    }

    internal static ResourceDictionary? FindPaletteDictionary(ResourceDictionary root)
    {
        if (root.Contains(TextBrushKey))
        {
            return root;
        }

        foreach (var merged in root.MergedDictionaries)
        {
            if (merged.Contains(TextBrushKey))
            {
                return merged;
            }
        }

        return null;
    }

    private static ResourceDictionary InsertPaletteDictionary(ResourceDictionary root)
    {
        var palette = new ResourceDictionary();
        root.MergedDictionaries.Insert(0, palette);
        return palette;
    }

    private static void CopyPaletteEntries(ResourceDictionary target, ResourceDictionary source)
    {
        foreach (var key in source.Keys)
        {
            target[key] = source[key];
        }
    }

    private static LinearGradientBrush ChatGradient(UiChromeColors c)
    {
        var brush = new LinearGradientBrush(c.ChatBackgroundTop, c.ChatBackgroundBottom, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Brush(Color color, double? opacity = null) =>
        AppThemeColor.ToFrozenBrush(color, opacity);
}
