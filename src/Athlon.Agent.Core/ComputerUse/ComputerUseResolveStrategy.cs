namespace Athlon.Agent.Core.ComputerUse;

/// <summary>
/// How a <c>computer_interact</c> action resolves its target. Recorded on observations
/// for telemetry and used by the host to pick the execution channel.
/// </summary>
public enum ComputerUseResolveStrategy
{
    /// <summary>No target could be resolved from the supplied arguments.</summary>
    None,

    /// <summary>
    /// Execute through the UI Automation element itself (Invoke/Value/Scroll patterns).
    /// Needs no pointer movement. Introduced in Phase 3; unreachable until enabled.
    /// </summary>
    ElementNative,

    /// <summary>Click coordinates derived from a UIA element's clickable point.</summary>
    ElementPoint,

    /// <summary>Screenshot pixel coordinates mapped to the physical desktop.</summary>
    ImagePoint,

    /// <summary>Caller-supplied physical desktop coordinates.</summary>
    PhysicalPoint
}

/// <summary>Action shapes shared by the host and the resolution rules, kept pure and testable.</summary>
public static class ComputerUseActionKinds
{
    /// <summary>Actions that must move the pointer to a coordinate.</summary>
    public static bool IsPointerAction(string? action) => action is
        "click" or "double_click" or "right_click" or "drag" or "scroll";

    /// <summary>
    /// Actions that do not reflow the window. These settle faster and, when a UI Automation
    /// pattern exists, can complete without moving the pointer.
    /// </summary>
    public static bool IsLayoutNeutralAction(string? action) => action is
        "type_text" or "key" or "hotkey" or "scroll";
}

/// <summary>
/// Decides which channel an interaction uses. Kept as a pure function so routing rules are covered
/// by unit tests instead of relying on the untested host.
/// </summary>
public static class ComputerUseResolveStrategyClassifier
{
    /// <summary>
    /// Element-native actions are limited to the ones UI Automation can express directly.
    /// <c>double_click</c> is deliberately excluded: Invoke's double-click semantics depend on the
    /// control, and <c>right_click</c> context menus are coordinate sensitive. <c>drag</c> has no
    /// UI Automation pattern at all.
    /// </summary>
    private static readonly HashSet<string> ElementNativeActions = new(StringComparer.Ordinal)
    {
        "click",
        "type_text",
        "key",
        "hotkey",
        "scroll"
    };

    /// <summary>True when the action has a potential element-native form.</summary>
    public static bool HasElementNativeForm(string? action) =>
        action is not null && ElementNativeActions.Contains(action);

    /// <summary>
    /// Static description of the channel implied by the arguments, before any UI Automation probe.
    /// This is the pre-Phase-3 (pointer-only) view, kept so telemetry can compare the historical
    /// routing against <see cref="Resolve"/>.
    /// </summary>
    public static ComputerUseResolveStrategy Classify(
        bool isPointerAction,
        bool hasElementId,
        bool hasImagePoint,
        bool hasPhysicalPoint)
    {
        if (isPointerAction && hasImagePoint)
        {
            return ComputerUseResolveStrategy.ImagePoint;
        }

        if (isPointerAction && hasElementId)
        {
            return ComputerUseResolveStrategy.ElementPoint;
        }

        if (isPointerAction && hasPhysicalPoint)
        {
            return ComputerUseResolveStrategy.PhysicalPoint;
        }

        if (!isPointerAction && hasElementId)
        {
            return ComputerUseResolveStrategy.ElementPoint;
        }

        return ComputerUseResolveStrategy.None;
    }

    /// <summary>
    /// Resolves the target channel including the element-native route.
    /// </summary>
    /// <remarks>
    /// Precedence is deliberate:
    /// <list type="number">
    /// <item>Screenshot pixels beat everything. An explicit image coordinate is a precise visual
    /// instruction and must not be silently replaced by a coarse element, which is the same rule
    /// <see cref="ComputerUsePointerTargetPolicy"/> already enforces.</item>
    /// <item>An <c>element_id</c> beats physical coordinates, matching the existing policy where the
    /// element's clickable point is preferred over raw desktop pixels.</item>
    /// <item>Element-native only applies when an element was supplied and the action has a native
    /// form; the host still has to confirm the control exposes a usable pattern and otherwise falls
    /// back to <see cref="ComputerUseResolveStrategy.ElementPoint"/>.</item>
    /// </list>
    /// </remarks>
    public static ComputerUseResolveStrategy Resolve(
        string? action,
        bool hasElementId,
        bool hasImagePoint,
        bool hasPhysicalPoint)
    {
        var isPointerAction = ComputerUseActionKinds.IsPointerAction(action);

        if (isPointerAction && hasImagePoint)
        {
            return ComputerUseResolveStrategy.ImagePoint;
        }

        if (hasElementId)
        {
            return HasElementNativeForm(action)
                ? ComputerUseResolveStrategy.ElementNative
                : ComputerUseResolveStrategy.ElementPoint;
        }

        if (isPointerAction && hasPhysicalPoint)
        {
            return ComputerUseResolveStrategy.PhysicalPoint;
        }

        return ComputerUseResolveStrategy.None;
    }

    /// <summary>
    /// Stable wire value used in audit records and tool payloads.
    /// </summary>
    public static string ToWireValue(ComputerUseResolveStrategy strategy) => strategy switch
    {
        ComputerUseResolveStrategy.ElementNative => "element_native",
        ComputerUseResolveStrategy.ElementPoint => "element_point",
        ComputerUseResolveStrategy.ImagePoint => "image_point",
        ComputerUseResolveStrategy.PhysicalPoint => "physical_point",
        _ => "none"
    };
}
