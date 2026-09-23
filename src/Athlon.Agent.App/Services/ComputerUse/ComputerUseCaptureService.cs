using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Athlon.Agent.Core;
using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.App.Services.ComputerUse;

public sealed record ComputerUseCapturedDesktop(
    byte[] ImageBytes,
    int Left,
    int Top,
    int Width,
    int Height,
    double DpiScale,
    int CursorX,
    int CursorY,
    int ImageWidth,
    int ImageHeight,
    string MimeType);

public sealed record ComputerUseDisplayState(
    int Left,
    int Top,
    int Width,
    int Height,
    double DpiScale,
    int CursorX,
    int CursorY);

/// <summary>Virtual-screen bounds of one monitor, in physical desktop pixels.</summary>
public readonly record struct ComputerUseMonitorBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

public sealed class ComputerUseCaptureService(AppSettings settings)
{
    private readonly ComputerUseSettings _settings = settings.ComputerUse;

    public ComputerUseCapturedDesktop CaptureCursorMonitor()
    {
        if (!GetCursorPos(out var cursor))
        {
            throw new InvalidOperationException("Unable to read the cursor position.");
        }

        return CaptureAt(cursor.X, cursor.Y);
    }

    /// <summary>
    /// Captures the monitor addressed by its position in the virtual-screen monitor list. Lets the
    /// model pick a display explicitly instead of relying on where the user's cursor happens to be.
    /// </summary>
    public ComputerUseCapturedDesktop CaptureMonitorIndex(int index)
    {
        var monitor = EnumerateMonitors();
        if (index < 0 || index >= monitor.Count)
        {
            throw new InvalidOperationException(
                $"monitor_index {index} is out of range; {monitor.Count} monitor(s) detected.");
        }

        var bounds = monitor[index];
        return CaptureRect(
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            ResolveDpiScale(MonitorFromPoint(new NativePoint { X = bounds.Left, Y = bounds.Top }, MonitorDefaultToNearest)),
            null,
            null);
    }

    /// <summary>Captures an arbitrary desktop rectangle (used for window-scoped observation).</summary>
    public ComputerUseCapturedDesktop CaptureRect(
        int left,
        int top,
        int width,
        int height,
        double dpiScale,
        int? cursorX,
        int? cursorY)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The requested capture rectangle is empty.");
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to acquire the desktop device context.");
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException("Unable to allocate the desktop capture device context.");
        }

        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("Unable to allocate the desktop capture bitmap.");
            }

            previous = SelectObject(memoryDc, bitmap);
            if (!BitBlt(
                    memoryDc,
                    0,
                    0,
                    width,
                    height,
                    screenDc,
                    left,
                    top,
                    SourceCopy | CaptureBlt))
            {
                throw new InvalidOperationException("Desktop capture failed.");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            var encoded = ComputerUseScreenshotEncoder.Encode(
                source,
                width,
                height,
                new ComputerUseScreenshotOptions(
                    _settings.ScreenshotMaxLongestEdge,
                    _settings.ScreenshotJpegQuality));
            var cursor = GetCursorPos(out var current) ? current : default;
            return new ComputerUseCapturedDesktop(
                encoded.Bytes,
                left,
                top,
                width,
                height,
                dpiScale,
                cursorX ?? cursor.X,
                cursorY ?? cursor.Y,
                encoded.ImageWidth,
                encoded.ImageHeight,
                encoded.MimeType);
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Monitor rectangles in virtual-screen order; used for <c>monitor_index</c> targeting.</summary>
    public static IReadOnlyList<ComputerUseMonitorBounds> EnumerateMonitors()
    {
        var monitors = new List<ComputerUseMonitorBounds>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(new ComputerUseMonitorBounds(
                    info.Monitor.Left,
                    info.Monitor.Top,
                    info.Monitor.Right,
                    info.Monitor.Bottom));
            }

            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    /// <summary>Bounds of the monitor containing the point, without capturing it.</summary>
    public static bool TryGetMonitorBoundsAt(int x, int y, out ComputerUseMonitorBounds bounds)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                bounds = new ComputerUseMonitorBounds(
                    info.Monitor.Left,
                    info.Monitor.Top,
                    info.Monitor.Right,
                    info.Monitor.Bottom);
                return true;
            }
        }

        bounds = default;
        return false;
    }

    /// <summary>
    /// Resolves a visible top-level window by title substring (optionally narrowed by process name).
    /// Targeting a window lets the agent work on a specific app without the user first bringing it
    /// to the foreground, which was the only way to steer Computer Use before.
    /// </summary>
    public static bool TryFindWindowBounds(
        string title,
        string? processName,
        out ComputerUseMonitorBounds bounds,
        out IntPtr handle)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            bounds = default;
            handle = IntPtr.Zero;
            return false;
        }

        // Captured into locals because an out parameter cannot be written from inside the callback.
        var found = false;
        var bestArea = long.MaxValue;
        var bestBounds = default(ComputerUseMonitorBounds);
        var bestHandle = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window))
            {
                return true;
            }

            var length = GetWindowTextLength(window);
            if (length <= 0)
            {
                return true;
            }

            var buffer = new StringBuilder(length + 1);
            if (GetWindowText(window, buffer, buffer.Capacity) <= 0)
            {
                return true;
            }

            if (buffer.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(processName)
                && !MatchesProcess(window, processName!))
            {
                return true;
            }

            if (!TryGetWindowRect(window, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                return true;
            }

            // Prefer the smallest matching window: a title substring often also matches a
            // taskbar/owned host window wrapping the real frame.
            var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area < bestArea)
            {
                bestArea = area;
                bestBounds = new ComputerUseMonitorBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
                bestHandle = window;
                found = true;
            }

            return true;
        }, IntPtr.Zero);

        bounds = bestBounds;
        handle = bestHandle;
        return found;
    }

    private static bool MatchesProcess(IntPtr window, string processName)
    {
        try
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return false;
            }

            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetWindowRect(IntPtr window, out NativeRect rect)
    {
        if (GetWindowRect(window, out var native))
        {
            // DwmGetWindowAttribute returns the visible frame; GetWindowRect includes the invisible
            // resize border on Windows 10+, which would shift every captured element by a few pixels.
            if (DwmGetWindowAttribute(window, DwmExtendedFrameBounds, out var extended, Marshal.SizeOf<NativeRect>()) == 0
                && extended.Right > extended.Left
                && extended.Bottom > extended.Top)
            {
                rect = extended;
                return true;
            }

            rect = native;
            return true;
        }

        rect = default;
        return false;
    }

    public ComputerUseCapturedDesktop CaptureAt(int x, int y)
    {
        var resolved = ResolveAt(x, y);
        var bounds = resolved.Bounds;
        return CaptureRect(
            bounds.Left,
            bounds.Top,
            resolved.State.Width,
            resolved.State.Height,
            resolved.State.DpiScale,
            resolved.State.CursorX,
            resolved.State.CursorY);
    }

    public ComputerUseDisplayState ProbeAt(int x, int y) => ResolveAt(x, y).State;

    /// <summary>DPI scale of the monitor at a point, without capturing anything.</summary>
    public static double ProbeDpiScaleAt(int x, int y)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
        return monitor == IntPtr.Zero ? 1 : ResolveDpiScale(monitor);
    }

    public ulong CaptureSignatureAt(int x, int y)
    {
        const int columns = 24;
        const int rows = 14;
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;

        var display = ResolveAt(x, y).State;
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to acquire the desktop device context.");
        }

        try
        {
            var hash = offset;
            var validSamples = 0;
            for (var row = 0; row < rows; row++)
            {
                var sampleY = display.Top
                    + Math.Clamp((row * display.Height + display.Height / 2) / rows, 0, display.Height - 1);
                for (var column = 0; column < columns; column++)
                {
                    var sampleX = display.Left
                        + Math.Clamp((column * display.Width + display.Width / 2) / columns, 0, display.Width - 1);
                    var color = GetPixel(screenDc, sampleX, sampleY);
                    if (color == InvalidColor)
                    {
                        continue;
                    }

                    validSamples++;
                    // Sparse, quantized samples ignore tiny rendering noise without paying
                    // for another full BitBlt + JPEG encode.
                    hash = (hash ^ (color & 0xF0)) * prime;
                    hash = (hash ^ ((color >> 8) & 0xF0)) * prime;
                    hash = (hash ^ ((color >> 16) & 0xF0)) * prime;
                }
            }

            if (validSamples == 0)
            {
                throw new InvalidOperationException("Unable to sample desktop pixels.");
            }

            return hash;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static ResolvedDisplay ResolveAt(int x, int y)
    {
        if (!GetCursorPos(out var cursor))
        {
            cursor = new NativePoint { X = x, Y = y };
        }

        var probe = new NativePoint { X = x, Y = y };
        var monitor = MonitorFromPoint(probe, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to resolve the active monitor.");
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            throw new InvalidOperationException("Unable to read monitor bounds.");
        }

        var bounds = info.Monitor;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The active monitor has invalid bounds.");
        }

        return new ResolvedDisplay(
            bounds,
            new ComputerUseDisplayState(
                bounds.Left,
                bounds.Top,
                width,
                height,
                ResolveDpiScale(monitor),
                cursor.X,
                cursor.Y));
    }

    private static double ResolveDpiScale(IntPtr monitor)
    {
        try
        {
            return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0
                ? Math.Max(1, dpiX / 96d)
                : 1;
        }
        catch (DllNotFoundException)
        {
            return 1;
        }
        catch (EntryPointNotFoundException)
        {
            return 1;
        }
    }

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint InvalidColor = 0xFFFFFFFF;
    private const int SourceCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    private sealed record ResolvedDisplay(
        NativeRect Bounds,
        ComputerUseDisplayState State);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRect,
        MonitorEnumProc callback,
        IntPtr data);

    private delegate bool WindowEnumProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    private const uint DwmExtendedFrameBounds = 9;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out NativeRect value,
        int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr deviceContext, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destination,
        int xDestination,
        int yDestination,
        int width,
        int height,
        IntPtr source,
        int xSource,
        int ySource,
        int rasterOperation);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}
