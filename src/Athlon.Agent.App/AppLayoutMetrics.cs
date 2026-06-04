namespace Athlon.Agent.App;

/// <summary>Shared layout sizes so split-pane chrome lines up.</summary>
public static class AppLayoutMetrics
{
    /// <summary>Height of the editor tab strip and chat session toolbar (must match).</summary>
    public const double SplitPaneHeaderHeight = 48;

    /// <summary>Gap between scrollbars and adjacent content.</summary>
    public const double ScrollBarGutter = 6;

    /// <summary>Hit target width/height of column/row splitters.</summary>
    public const double SplitterHitSize = 12;

    /// <summary>Inset of the visible splitter line inside the hit target.</summary>
    public const double SplitterLineInset = 4;
}
