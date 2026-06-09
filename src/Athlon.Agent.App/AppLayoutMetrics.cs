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

    /// <summary>Inset of the visible splitter line inside the hit target.</summary>
    public const double SplitterLineInset = 0;
}
