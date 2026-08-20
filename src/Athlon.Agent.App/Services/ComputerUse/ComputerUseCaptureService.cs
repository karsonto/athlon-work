using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

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

public sealed class ComputerUseCaptureService
{
    public ComputerUseCapturedDesktop CaptureCursorMonitor()
    {
        if (!GetCursorPos(out var cursor))
        {
            throw new InvalidOperationException("Unable to read the cursor position.");
        }

        return CaptureAt(cursor.X, cursor.Y);
    }

    public ComputerUseCapturedDesktop CaptureAt(int x, int y)
    {
        var resolved = ResolveAt(x, y);
        var bounds = resolved.Bounds;
        var width = resolved.State.Width;
        var height = resolved.State.Height;

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
                    bounds.Left,
                    bounds.Top,
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

            var encoded = ComputerUseScreenshotEncoder.Encode(source, width, height);
            return new ComputerUseCapturedDesktop(
                encoded.Bytes,
                bounds.Left,
                bounds.Top,
                width,
                height,
                resolved.State.DpiScale,
                resolved.State.CursorX,
                resolved.State.CursorY,
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

    public ComputerUseDisplayState ProbeAt(int x, int y) => ResolveAt(x, y).State;

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
