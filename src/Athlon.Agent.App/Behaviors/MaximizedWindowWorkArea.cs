using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Athlon.Agent.App.Behaviors;

/// <summary>
/// Keeps borderless maximized windows inside the monitor work area (above the taskbar),
/// so bottom chrome such as the sidebar account bar stays visible.
/// </summary>
internal static class MaximizedWindowWorkArea
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private static readonly DependencyProperty HookAttachedProperty =
        DependencyProperty.RegisterAttached(
            "HookAttached",
            typeof(bool),
            typeof(MaximizedWindowWorkArea),
            new PropertyMetadata(false));

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if ((bool)window.GetValue(HookAttachedProperty))
        {
            return;
        }

        window.SetValue(HookAttachedProperty, true);

        if (window.IsLoaded || PresentationSource.FromVisual(window) is not null)
        {
            Hook(window);
            return;
        }

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            Hook(window);
        }

        window.SourceInitialized += OnSourceInitialized;
    }

    private static void Hook(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var work = monitorInfo.RcWork;
                var monitorRect = monitorInfo.RcMonitor;
                mmi.PtMaxPosition.X = Math.Abs(work.Left - monitorRect.Left);
                mmi.PtMaxPosition.Y = Math.Abs(work.Top - monitorRect.Top);
                mmi.PtMaxSize.X = Math.Abs(work.Right - work.Left);
                mmi.PtMaxSize.Y = Math.Abs(work.Bottom - work.Top);
            }
        }

        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: true);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point PtReserved;
        public Point PtMaxSize;
        public Point PtMaxPosition;
        public Point PtMinTrackSize;
        public Point PtMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;
    }
}
