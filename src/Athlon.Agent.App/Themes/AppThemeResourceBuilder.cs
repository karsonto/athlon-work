using System.Windows;
using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

internal static class AppThemeResourceBuilder
{
    internal const string PaletteMarkerKey = "__AthlonChromePalette__";
    internal const string TextBrushKey = "Brush.Text";

    public static ResourceDictionary BuildChromeResources(AppThemePalette palette) =>
        BuildChromeResources(palette.Chrome, palette.Teams);

    public static ResourceDictionary BuildChromeResources(UiChromeColors c, UiTeamsColors teams)
    {
        var resources = new ResourceDictionary
        {
            [PaletteMarkerKey] = true,
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
            ["Brush.ModeAgentBg"] = Brush(c.ModeAgentBg),
            ["Brush.ModeAgentBorder"] = Brush(c.ModeAgentBorder),
            ["Brush.ModeAgentForeground"] = Brush(c.ModeAgentForeground),
            ["Brush.ModeCodingBg"] = Brush(c.ModeCodingBg),
            ["Brush.ModeCodingBorder"] = Brush(c.ModeCodingBorder),
            ["Brush.ModeCodingForeground"] = Brush(c.ModeCodingForeground),
            ["Brush.ModeAskBg"] = Brush(c.ModeAskBg),
            ["Brush.ModeAskBorder"] = Brush(c.ModeAskBorder),
            ["Brush.ModeAskForeground"] = Brush(c.ModeAskForeground),
            ["Brush.ModePlanBg"] = Brush(c.ModePlanBg),
            ["Brush.ModePlanBorder"] = Brush(c.ModePlanBorder),
            ["Brush.ModePlanForeground"] = Brush(c.ModePlanForeground),
            ["Brush.ModeDebugBg"] = Brush(c.ModeDebugBg),
            ["Brush.ModeDebugBorder"] = Brush(c.ModeDebugBorder),
            ["Brush.ModeDebugForeground"] = Brush(c.ModeDebugForeground),
            ["Brush.ModeTeamsBg"] = Brush(c.ModeTeamsBg),
            ["Brush.ModeTeamsBorder"] = Brush(c.ModeTeamsBorder),
            ["Brush.ModeTeamsForeground"] = Brush(c.ModeTeamsForeground),
            ["Brush.SurfaceHover"] = Brush(c.SurfaceHover),
            ["Brush.SurfaceActive"] = Brush(c.SurfaceActive),
            ["Brush.UserBubble"] = Brush(c.UserBubble, c.UserBubbleOpacity),
            ["Brush.AssistantBubble"] = Brush(c.AssistantBubble),
            ["Brush.Success"] = Brush(c.Success),
            ["Brush.SuccessSubtle"] = Brush(c.SuccessSubtle),
            ["Brush.OnSuccess"] = Brush(c.OnSuccess),
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
            ["Brush.AtCompletionMcpBadgeBg"] = Brush(c.AtCompletionMcpBadgeBg),
            ["Brush.AtCompletionMcpBadgeBorder"] = Brush(c.AtCompletionMcpBadgeBorder),
            ["Brush.AtCompletionMcpBadgeText"] = Brush(c.AtCompletionMcpBadgeText),
            ["Brush.AtCompletionCommandBadgeBg"] = Brush(c.AtCompletionCommandBadgeBg),
            ["Brush.AtCompletionCommandBadgeBorder"] = Brush(c.AtCompletionCommandBadgeBorder),
            ["Brush.AtCompletionCommandBadgeText"] = Brush(c.AtCompletionCommandBadgeText),
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
            ["Brush.ChatBackground"] = Brush(c.ChatBackgroundTop),
            ["Brush.Teams.Leader"] = Brush(teams.Leader),
            ["Brush.Teams.LeaderSoft"] = Brush(teams.LeaderSoft),
            ["Brush.Teams.Worker"] = Brush(teams.Worker),
            ["Brush.Teams.WorkerSoft"] = Brush(teams.WorkerSoft),
            ["Brush.Teams.Success"] = Brush(teams.Success),
            ["Brush.Teams.SuccessSoft"] = Brush(teams.SuccessSoft),
            ["Brush.Teams.Warn"] = Brush(teams.Warn),
            ["Brush.Teams.WarnSoft"] = Brush(teams.WarnSoft),
            ["Brush.Teams.Error"] = Brush(teams.Error),
            ["Brush.Teams.ErrorSoft"] = Brush(teams.ErrorSoft),
            ["Brush.Teams.ErrorBorder"] = Brush(teams.ErrorBorder),
            ["Brush.Teams.AvatarWorker"] = DiagonalGradient(teams.AvatarWorkerStart, teams.AvatarWorkerEnd),
            ["Brush.Teams.AvatarLeader"] = DiagonalGradient(teams.AvatarLeaderStart, teams.AvatarLeaderEnd),
            ["Brush.Teams.OrgPreview"] = DiagonalGradient(
                teams.OrgPreviewStart,
                teams.OrgPreviewMid,
                teams.OrgPreviewEnd,
                midOffset: 0.55),
        };

        // The app loads MaterialDesign's control styles without installing its theme, so the keys
        // those styles resolve are absent unless supplied here. See MaterialDesignBrushBridge.
        foreach (var (key, value) in MaterialDesignBrushBridge.Build(c))
        {
            resources[key] = value;
        }

        return resources;
    }

    public static void ApplyPalette(ResourceDictionary root, AppThemePalette palette)
    {
        var newPalette = BuildChromeResources(palette);
        var existing = FindPaletteDictionary(root);
        if (existing is null)
        {
            root.MergedDictionaries.Insert(0, newPalette);
            return;
        }

        var index = root.MergedDictionaries.IndexOf(existing);
        if (index < 0)
        {
            root.MergedDictionaries.Insert(0, newPalette);
            return;
        }

        // Keep the palette dictionary instance stable. Removing and reinserting the
        // dictionary briefly invalidates DynamicResource lookups, which is visible as
        // a full-surface flash during theme switches.
        foreach (var key in newPalette.Keys)
        {
            existing[key] = newPalette[key];
        }
    }

    internal static ResourceDictionary? FindPaletteDictionary(ResourceDictionary root)
    {
        foreach (var merged in root.MergedDictionaries)
        {
            if (merged.Contains(PaletteMarkerKey))
            {
                return merged;
            }
        }

        if (root.Contains(PaletteMarkerKey))
        {
            return root;
        }

        return null;
    }

    private static SolidColorBrush Brush(Color color, double? opacity = null) =>
        AppThemeColor.ToFrozenBrush(color, opacity);

    private static LinearGradientBrush DiagonalGradient(Color start, Color end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(start, 0),
                new GradientStop(end, 1),
            },
        };
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush DiagonalGradient(
        Color start,
        Color mid,
        Color end,
        double midOffset)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(start, 0),
                new GradientStop(mid, midOffset),
                new GradientStop(end, 1),
            },
        };
        brush.Freeze();
        return brush;
    }
}
