namespace Athlon.Agent.Core.ComputerUse;

/// <summary>
/// UI Automation patterns the host can use to execute an action without moving the pointer.
/// Values mirror the Windows <c>AutomationPattern</c> programmatic names so the App layer can map
/// them back without guessing.
/// </summary>
public enum ComputerUseElementPattern
{
    None,
    Invoke,
    SelectionItem,
    Toggle,
    ExpandCollapse,
    Value,
    Scroll,
    ScrollItem
}

/// <summary>
/// Errors surfaced when an element-native action cannot be completed. Distinct from
/// <c>element_gone</c> so callers can tell "the element is still there but cannot be driven
/// natively" (fall back to coordinates) from "the element disappeared" (re-observe).
/// </summary>
public static class ComputerUseElementActionErrors
{
    public const string PatternUnavailable = "element_pattern_unavailable";

    public const string ReadOnly = "element_readonly";

    public const string WriteNotApplied = "element_write_not_applied";
}

/// <summary>
/// Result of choosing a pattern for an element-native action. Pure so the selection order can be
/// unit tested; the App layer only probes whether each candidate is actually available.
/// </summary>
public sealed record ComputerUseElementActionPlan(
    ComputerUseElementPattern Pattern,
    string? Value,
    bool VerifyWrite);

/// <summary>
/// Outcome of attempting an element-native action. <see cref="Applied"/> false means no pattern
/// worked and the caller must fall back to coordinate input.
/// </summary>
public sealed record ComputerUseElementActionResult(
    bool Applied,
    string? ErrorCode,
    string? Detail)
{
    public static ComputerUseElementActionResult Success() => new(true, null, null);

    public static ComputerUseElementActionResult Unavailable() =>
        new(false, ComputerUseElementActionErrors.PatternUnavailable, null);

    public static ComputerUseElementActionResult ReadOnly() =>
        new(false, ComputerUseElementActionErrors.ReadOnly, null);

    public static ComputerUseElementActionResult WriteNotApplied(string? actual) =>
        new(false, ComputerUseElementActionErrors.WriteNotApplied, actual);
}

/// <summary>
/// Maps a Computer Use action onto the UI Automation pattern that expresses it natively.
/// </summary>
/// <remarks>
/// Ordered fallbacks mirror how Windows exposes these controls: a button is usually Invoke, a list
/// entry is SelectionItem, a checkbox is Toggle, and a tree/collapsible section is ExpandCollapse.
/// Returning a plan does not guarantee the element supports the pattern — the host probes with
/// <c>TryGetCurrentPattern</c> and walks the fallback chain, then degrades to coordinate input.
/// </remarks>
public static class ComputerUseElementActionResolver
{
    private static readonly ComputerUseElementPattern[] ClickFallbacks =
    [
        ComputerUseElementPattern.Invoke,
        ComputerUseElementPattern.SelectionItem,
        ComputerUseElementPattern.Toggle,
        ComputerUseElementPattern.ExpandCollapse
    ];

    private static readonly ComputerUseElementPattern[] ScrollFallbacks =
    [
        ComputerUseElementPattern.Scroll,
        ComputerUseElementPattern.ScrollItem
    ];

    /// <summary>
    /// Candidate patterns for <paramref name="action"/>, most-preferred first. Empty when the action
    /// has no element-native form (drag, right_click, double_click).
    /// </summary>
    public static IReadOnlyList<ComputerUseElementPattern> CandidatePatterns(
        string? action,
        string? key)
    {
        switch (action)
        {
            case "click":
                return ClickFallbacks;
            case "type_text":
                return [ComputerUseElementPattern.Value];
            case "scroll":
                return ScrollFallbacks;
            case "key" or "hotkey":
                // Only activation keys have an unambiguous native equivalent. Everything else
                // (navigation, function keys, shortcuts) must be delivered as real key input.
                return IsActivationKey(key)
                    ? ClickFallbacks
                    : [];
            default:
                return [];
        }
    }

    /// <summary>
    /// Builds the plan for an action, or null when the action has no element-native form.
    /// </summary>
    public static ComputerUseElementActionPlan? Plan(
        string? action,
        string? key,
        int scrollDelta,
        string? text)
    {
        switch (action)
        {
            case "click":
                return new ComputerUseElementActionPlan(
                    ComputerUseElementPattern.Invoke,
                    Value: null,
                    VerifyWrite: false);
            case "type_text":
                if (text is null)
                {
                    return null;
                }

                // ValuePattern.SetValue is often rejected (password/read-only) or silently coerced
                // (masked formatting), so the host must read the value back and compare.
                return new ComputerUseElementActionPlan(
                    ComputerUseElementPattern.Value,
                    Value: text,
                    VerifyWrite: true);
            case "scroll":
                return new ComputerUseElementActionPlan(
                    ComputerUseElementPattern.Scroll,
                    Value: scrollDelta.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    VerifyWrite: false);
            case "key" or "hotkey":
                return IsActivationKey(key)
                    ? new ComputerUseElementActionPlan(
                        ComputerUseElementPattern.Invoke,
                        Value: null,
                        VerifyWrite: false)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>True for keys whose only meaning is "activate the focused control".</summary>
    public static bool IsActivationKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Trim().ToUpperInvariant();
        // Hotkey expressions contain '+'; only a bare activation key qualifies.
        return normalized is "ENTER" or "RETURN" or "SPACE";
    }
}
