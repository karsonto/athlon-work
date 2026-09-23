using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.App.Services.ComputerUse;

public sealed record ComputerUseUiSnapshot(
    string Json,
    IReadOnlyDictionary<string, AutomationElement> Elements,
    string ForegroundWindowTitle,
    string ForegroundProcessName,
    nint ForegroundWindowHandle);

public sealed record ComputerUseForegroundWindow(
    nint Handle,
    string Title,
    string ProcessName);

public sealed class ComputerUseUiAutomationService
{
    public ComputerUseUiSnapshot Capture(
        int maxDepth,
        int maxNodes,
        int? monitorLeft = null,
        int? monitorTop = null,
        int? monitorWidth = null,
        int? monitorHeight = null,
        int? imageWidth = null,
        int? imageHeight = null,
        nint rootHandle = default)
    {
        // A window-targeted observation walks the requested window; everything else follows the
        // foreground window as before.
        var foreground = rootHandle != IntPtr.Zero ? rootHandle : GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return EmptySnapshot();
        }

        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(foreground);
        }
        catch (ElementNotAvailableException)
        {
            return EmptySnapshot();
        }

        if (root is null)
        {
            return EmptySnapshot();
        }

        var elements = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
        var nodes = new StringBuilder();
        var walker = TreeWalker.ControlViewWalker;
        var nextId = 1;
        var nodeCount = 0;

        void Visit(AutomationElement element, string? parentId, int depth)
        {
            if (depth > maxDepth || nodeCount >= maxNodes)
            {
                return;
            }

            try
            {
                var current = element.Current;
                var bounds = current.BoundingRectangle;
                var include = ComputerUseUiNodeFilter.ShouldInclude(
                    isRoot: depth == 0,
                    isOffscreen: current.IsOffscreen,
                    boundsWidth: bounds.IsEmpty ? 0 : bounds.Width,
                    boundsHeight: bounds.IsEmpty ? 0 : bounds.Height,
                    monitorLeft,
                    monitorTop,
                    monitorWidth,
                    monitorHeight,
                    bounds.IsEmpty ? null : bounds.Left,
                    bounds.IsEmpty ? null : bounds.Top);

                string? id = null;
                if (include)
                {
                    id = $"ui_{nextId++}";
                    elements[id] = element;
                    if (nodeCount > 0)
                    {
                        nodes.Append(',');
                    }

                    nodes.Append('\n');
                    ComputerUseUiTreeWriter.WriteNode(
                        nodes,
                        id,
                        parentId,
                        depth,
                        current.Name,
                        NormalizeControlType(current.ControlType),
                        current.AutomationId,
                        current.IsEnabled,
                        current.IsOffscreen,
                        current.IsKeyboardFocusable,
                        bounds.IsEmpty ? null : (int)Math.Round(bounds.Left),
                        bounds.IsEmpty ? null : (int)Math.Round(bounds.Top),
                        bounds.IsEmpty ? null : (int)Math.Round(bounds.Width),
                        bounds.IsEmpty ? null : (int)Math.Round(bounds.Height),
                        monitorLeft,
                        monitorTop,
                        monitorWidth,
                        monitorHeight,
                        imageWidth,
                        imageHeight);
                    nodeCount++;
                }

                // Continue walking children even when the parent was filtered so nested
                // on-screen controls remain reachable under a later included ancestor.
                var childParentId = id ?? parentId;
                var child = walker.GetFirstChild(element);
                while (child is not null && nodeCount < maxNodes)
                {
                    Visit(child, childParentId, depth + 1);
                    child = walker.GetNextSibling(child);
                }
            }
            catch (ElementNotAvailableException)
            {
                // The desktop changed while the bounded snapshot was being collected.
            }
        }

        Visit(root, null, 0);
        var json = nodeCount == 0
            ? "[]"
            : $"[{nodes}\n]";
        return new ComputerUseUiSnapshot(
            json,
            elements,
            SafeCurrent(root, static current => current.Name),
            ResolveProcessName(root),
            foreground);
    }

    public bool TryGetClickablePoint(AutomationElement element, out int x, out int y)
    {
        x = 0;
        y = 0;
        try
        {
            if (element.TryGetClickablePoint(out var point))
            {
                x = (int)Math.Round(point.X);
                y = (int)Math.Round(point.Y);
                return true;
            }

            var bounds = element.Current.BoundingRectangle;
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                x = (int)Math.Round(bounds.Left + (bounds.Width / 2));
                y = (int)Math.Round(bounds.Top + (bounds.Height / 2));
                return true;
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return false;
    }

    /// <summary>
    /// Attempts to execute an action through the element's own UI Automation pattern so the pointer
    /// never moves. Probing and invoking happen in the same call to avoid a time-of-check /
    /// time-of-use gap between "does this pattern exist" and "use it".
    /// </summary>
    /// <remarks>
    /// Must run inside <c>RunBoundedUiAutomationAsync</c>: cross-process marshalling can block, and
    /// the host relies on that wrapper for the timeout and concurrency limits. Returns a failure
    /// reason instead of throwing so the caller can fall back to coordinate input.
    /// </remarks>
    public ComputerUseElementActionResult TryExecuteElementAction(
        AutomationElement element,
        string action,
        string? key,
        int scrollDelta,
        string? text)
    {
        ArgumentNullException.ThrowIfNull(element);
        var patterns = ComputerUseElementActionResolver.CandidatePatterns(action, key);
        if (patterns.Count == 0)
        {
            return ComputerUseElementActionResult.Unavailable();
        }

        if (ComputerUseElementActionResolver.IsActivationKey(key))
        {
            // Keys were already validated by the host; route them to the activation patterns.
            patterns = ComputerUseElementActionResolver.CandidatePatterns("click", key: null);
        }

        foreach (var candidate in patterns)
        {
            var outcome = TryApplyPattern(element, candidate, scrollDelta, text);
            if (outcome is not null)
            {
                return outcome;
            }
        }

        return ComputerUseElementActionResult.Unavailable();
    }

    private static ComputerUseElementActionResult? TryApplyPattern(
        AutomationElement element,
        ComputerUseElementPattern pattern,
        int scrollDelta,
        string? text)
    {
        switch (pattern)
        {
            case ComputerUseElementPattern.Invoke:
                if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                {
                    return null;
                }

                ((InvokePattern)invoke).Invoke();
                return ComputerUseElementActionResult.Success();
            case ComputerUseElementPattern.SelectionItem:
                if (!element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
                {
                    return null;
                }

                ((SelectionItemPattern)selection).Select();
                return ComputerUseElementActionResult.Success();
            case ComputerUseElementPattern.Toggle:
                if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
                {
                    return null;
                }

                ((TogglePattern)toggle).Toggle();
                return ComputerUseElementActionResult.Success();
            case ComputerUseElementPattern.ExpandCollapse:
                if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
                {
                    return null;
                }

                ((ExpandCollapsePattern)expand).Expand();
                return ComputerUseElementActionResult.Success();
            case ComputerUseElementPattern.Value:
                if (text is null || !element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
                {
                    return null;
                }

                var valuePattern = (ValuePattern)value;
                if (valuePattern.Current.IsReadOnly)
                {
                    return ComputerUseElementActionResult.ReadOnly();
                }

                valuePattern.SetValue(text);
                // Write-back verification: masked/validating controls accept the call and then
                // discard or reformat the text, which would otherwise look like success.
                var written = valuePattern.Current.Value;
                return string.Equals(written, text, StringComparison.Ordinal)
                    ? ComputerUseElementActionResult.Success()
                    : ComputerUseElementActionResult.WriteNotApplied(written);
            case ComputerUseElementPattern.Scroll:
                if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll))
                {
                    return null;
                }

                var scrollPattern = (ScrollPattern)scroll;
                if (scrollPattern.Current.VerticallyScrollable)
                {
                    scrollPattern.Scroll(
                        System.Windows.Automation.ScrollAmount.NoAmount,
                        scrollDelta >= 0 ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement);
                    return ComputerUseElementActionResult.Success();
                }

                if (scrollPattern.Current.HorizontallyScrollable)
                {
                    scrollPattern.Scroll(
                        scrollDelta >= 0 ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement,
                        ScrollAmount.NoAmount);
                    return ComputerUseElementActionResult.Success();
                }

                return null;
            case ComputerUseElementPattern.ScrollItem:
                if (!element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scrollItem))
                {
                    return null;
                }

                ((ScrollItemPattern)scrollItem).ScrollIntoView();
                return ComputerUseElementActionResult.Success();
            default:
                return null;
        }
    }

    public bool MatchesCurrentDesktop(string? elementId, string? name)
    {
        var snapshot = Capture(maxDepth: 8, maxNodes: 600);
        if (!string.IsNullOrWhiteSpace(elementId) && snapshot.Elements.ContainsKey(elementId))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var element in snapshot.Elements.Values)
        {
            try
            {
                if (element.Current.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        return false;
    }

    public static bool IsAvailable(AutomationElement element)
    {
        try
        {
            _ = element.Current.ProcessId;
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    public string GetForegroundWindowTitle()
        => GetForegroundWindowIdentity().Title;

    public ComputerUseForegroundWindow GetForegroundWindowIdentity()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return new ComputerUseForegroundWindow(0, string.Empty, string.Empty);
        }

        try
        {
            var element = AutomationElement.FromHandle(handle);
            return element is null
                ? new ComputerUseForegroundWindow(handle, string.Empty, string.Empty)
                : new ComputerUseForegroundWindow(
                    handle,
                    SafeCurrent(element, static current => current.Name),
                    ResolveProcessName(element));
        }
        catch (ElementNotAvailableException)
        {
            return new ComputerUseForegroundWindow(handle, string.Empty, string.Empty);
        }
    }

    private static string NormalizeControlType(ControlType? controlType) =>
        controlType?.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal)
        ?? "Unknown";

    private static string ResolveProcessName(AutomationElement root)
    {
        try
        {
            var processId = root.Current.ProcessId;
            return processId > 0 ? Process.GetProcessById(processId).ProcessName : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeCurrent(
        AutomationElement element,
        Func<AutomationElement.AutomationElementInformation, string> selector)
    {
        try
        {
            return selector(element.Current);
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    private static ComputerUseUiSnapshot EmptySnapshot() =>
        new("[]", new Dictionary<string, AutomationElement>(), string.Empty, string.Empty, 0);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
