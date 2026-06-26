using System.Windows;

namespace Athlon.Agent.App;

/// <summary>Shared layout sizes so split-pane chrome lines up.</summary>
public static class AppLayoutMetrics
{
    /// <summary>Height of the editor tab strip and chat session toolbar (must match).</summary>
    public const double SplitPaneHeaderHeight = 56;

    /// <summary>Custom window caption strip (minimize / maximize / close only).</summary>
    public const double WindowChromeHeight = 32;

    /// <summary>Per-panel header height (sidebar, chat, context).</summary>
    public const double PanelHeaderHeight = 56;

    /// <summary><see cref="WindowChromeHeight"/> as <see cref="GridLength"/> for row definitions.</summary>
    public static readonly GridLength WindowChromeRowHeight = new(WindowChromeHeight);

    /// <summary><see cref="PanelHeaderHeight"/> as <see cref="GridLength"/> for row definitions.</summary>
    public static readonly GridLength PanelHeaderRowHeight = new(PanelHeaderHeight);

    /// <summary>Gap between scrollbars and adjacent content.</summary>
    public const double ScrollBarGutter = 6;

    /// <summary>Hit target width/height of column/row splitters.</summary>
    public const double SplitterHitSize = 12;

    /// <summary>Max content width for the chat composer (used with 85% column layout).</summary>
    public const double ComposerMaxContentWidth = 1120;

    /// <summary>Inset of the visible splitter line inside the hit target.</summary>
    public const double SplitterLineInset = 0;
}

/// <summary>Min/max/default sizes for resizable shell panes.</summary>
public static class UiLayoutConstraints
{
    public const double ContextSidebarMinWidth = 220;
    public const double ContextSidebarMaxWidth = 560;
    public const double ContextSidebarDefaultWidth = 320;
    public const double ContextSidebarCollapseDragThreshold = 200;

    public const double NavigationSidebarMinWidth = 180;
    public const double NavigationSidebarMaxWidth = 480;
    public const double NavigationSidebarDefaultWidth = 280;

    public const double EditorPaneMinWidth = 280;
    public const double EditorPaneMaxWidth = 1200;
    public const double EditorPaneDefaultWidth = 480;

    public const double ComposerMinHeight = 120;
    public const double ComposerMaxHeight = 420;
    public const double ComposerDefaultHeight = 168;
}
