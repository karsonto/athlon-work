namespace Athlon.Agent.Core.ComputerUse;

/// <summary>
/// Tunables for the Computer Use runtime. Every value here replaces a previously
/// hardcoded constant, so all defaults preserve the pre-optimization behavior.
/// </summary>
public sealed class ComputerUseSettings
{
    // ---- Observation shape (token + UIA traversal cost) --------------------

    /// <summary>Default for <c>computer_observe.max_tree_depth</c> when the model omits it.</summary>
    public int DefaultMaxTreeDepth { get; set; } = ComputerUseSettingsDefaults.DefaultMaxTreeDepth;

    /// <summary>Default for <c>computer_observe.max_nodes</c> when the model omits it.</summary>
    public int DefaultMaxNodes { get; set; } = ComputerUseSettingsDefaults.DefaultMaxNodes;

    // ---- Screenshot encoding (token + bandwidth) --------------------------

    /// <summary>Longest edge of the encoded screenshot in pixels; larger frames are downscaled.</summary>
    public int ScreenshotMaxLongestEdge { get; set; } = 1600;

    /// <summary>JPEG quality level (1-100) for encoded screenshots.</summary>
    public int ScreenshotJpegQuality { get; set; } = 80;

    /// <summary>
    /// When true, keyboard-only actions (type_text / key / hotkey) skip the post-action
    /// screenshot and let the model call computer_observe when it needs to verify.
    /// </summary>
    public bool SkipScreenshotForKeyboardActions { get; set; }

    // ---- Post-action settling (latency) -----------------------------------

    /// <summary>Delay between desktop stability samples.</summary>
    public int SettleSampleIntervalMs { get; set; } = 75;

    /// <summary>Upper bound on stability samples before giving up.</summary>
    public int SettleMaxSamples { get; set; } = 8;

    /// <summary>Minimum samples before a stable verdict may be returned.</summary>
    public int SettleMinimumSamples { get; set; } = 4;

    /// <summary>Minimum samples for layout-neutral actions (type_text / key / scroll).</summary>
    public int SettleKeyboardMinimumSamples { get; set; } = 2;

    /// <summary>Consecutive identical signatures required to call the desktop stable.</summary>
    public int SettleRequiredConsecutiveMatches { get; set; } = 2;

    /// <summary>Fallback delay when the display driver cannot provide sampled pixels.</summary>
    public int SettleFallbackDelayMs { get; set; } = 250;

    // ---- Overlay handling (latency) ---------------------------------------

    /// <summary>Wait after hiding the Computer Use overlay before capturing or acting.</summary>
    public int OverlayHideDelayMs { get; set; } = 80;

    // ---- UI Automation bounds (latency) -----------------------------------

    /// <summary>Timeout for a single bounded UI Automation call. 0 or less uses the default.</summary>
    public int UiaCallTimeoutMs { get; set; } = 5000;

    /// <summary>Maximum concurrent in-flight UI Automation calls.</summary>
    public int UiaMaxConcurrentCalls { get; set; } = 4;

    // ---- Pointer input (latency) ------------------------------------------

    /// <summary>Delay between the two clicks of a double_click.</summary>
    public int DoubleClickIntervalMs { get; set; } = 80;

    /// <summary>Delay after mouse-down before drag movement starts, so the target can arm.</summary>
    public int DragArmDelayMs { get; set; } = 120;

    /// <summary>Interpolated movement steps for a drag.</summary>
    public int DragSteps { get; set; } = 16;

    /// <summary>Delay between drag movement steps.</summary>
    public int DragStepDelayMs { get; set; } = 16;

    /// <summary>Delay with the button held before releasing at the drag destination.</summary>
    public int DragHoldDelayMs { get; set; } = 80;

    // ---- Desktop capture session (scheduled runs) -------------------------

    /// <summary>Wait after minimizing the shell before BitBlt in unattended (scheduled) runs.</summary>
    public int DesktopCaptureSettleDelayMs { get; set; } = 150;

    // ---- Attachment lifecycle (disk) --------------------------------------

    /// <summary>
    /// Maximum number of Computer Use frame screenshots kept per session on disk.
    /// Zero or less disables pruning.
    /// </summary>
    public int MaxScreenshotsPerSession { get; set; } = 200;

    /// <summary>Age in minutes after which Computer Use frame screenshots are deleted. Zero disables.</summary>
    public int ScreenshotRetentionMinutes { get; set; } = 1440;
}
