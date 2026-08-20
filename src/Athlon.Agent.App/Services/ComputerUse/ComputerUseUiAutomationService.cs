using System.Diagnostics;
using System.Runtime.InteropServices;
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
        int? imageHeight = null)
    {
        var foreground = GetForegroundWindow();
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
        var nodes = new List<object>();
        var walker = TreeWalker.ControlViewWalker;
        var nextId = 1;

        void Visit(AutomationElement element, string? parentId, int depth)
        {
            if (depth > maxDepth || nodes.Count >= maxNodes)
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
                    object? imageBounds = null;
                    if (!bounds.IsEmpty
                        && monitorLeft is int captureLeft
                        && monitorTop is int captureTop
                        && monitorWidth is int captureWidth
                        && monitorHeight is int captureHeight
                        && imageWidth is > 0
                        && imageHeight is > 0)
                    {
                        var mapped = ComputerUseCoordinateMapper.PhysicalRectToImage(
                            (int)Math.Round(bounds.Left),
                            (int)Math.Round(bounds.Top),
                            (int)Math.Round(bounds.Width),
                            (int)Math.Round(bounds.Height),
                            captureLeft,
                            captureTop,
                            captureWidth,
                            captureHeight,
                            imageWidth.Value,
                            imageHeight.Value);
                        imageBounds = new
                        {
                            x = mapped.X,
                            y = mapped.Y,
                            width = mapped.Width,
                            height = mapped.Height
                        };
                    }

                    nodes.Add(new
                    {
                        element_id = id,
                        parent_id = parentId,
                        depth,
                        name = current.Name,
                        control_type = NormalizeControlType(current.ControlType),
                        automation_id = current.AutomationId,
                        enabled = current.IsEnabled,
                        offscreen = current.IsOffscreen,
                        focusable = current.IsKeyboardFocusable,
                        bounds = bounds.IsEmpty
                            ? null
                            : new
                            {
                                x = (int)Math.Round(bounds.Left),
                                y = (int)Math.Round(bounds.Top),
                                width = (int)Math.Round(bounds.Width),
                                height = (int)Math.Round(bounds.Height)
                            },
                        image_bounds = imageBounds
                    });
                }

                // Continue walking children even when the parent was filtered so nested
                // on-screen controls remain reachable under a later included ancestor.
                var childParentId = id ?? parentId;
                var child = walker.GetFirstChild(element);
                while (child is not null && nodes.Count < maxNodes)
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
        return new ComputerUseUiSnapshot(
            JsonSerializer.Serialize(nodes),
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
