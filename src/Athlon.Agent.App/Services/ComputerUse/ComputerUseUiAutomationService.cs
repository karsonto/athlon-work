using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace Athlon.Agent.App.Services.ComputerUse;

public sealed record ComputerUseUiSnapshot(
    string Json,
    IReadOnlyDictionary<string, AutomationElement> Elements,
    string ForegroundWindowTitle,
    string ForegroundProcessName);

public sealed class ComputerUseUiAutomationService
{
    public ComputerUseUiSnapshot Capture(int maxDepth, int maxNodes)
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
                var id = $"ui_{nextId++}";
                elements[id] = element;
                var bounds = current.BoundingRectangle;
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
                        }
                });

                var child = walker.GetFirstChild(element);
                while (child is not null && nodes.Count < maxNodes)
                {
                    Visit(child, id, depth + 1);
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
            ResolveProcessName(root));
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
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return AutomationElement.FromHandle(handle)?.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
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
        new("[]", new Dictionary<string, AutomationElement>(), string.Empty, string.Empty);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
