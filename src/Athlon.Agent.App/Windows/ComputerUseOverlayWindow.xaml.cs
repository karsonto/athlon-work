using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Windows;

public partial class ComputerUseOverlayWindow : Window
{
    private const double DefaultWidth = 784;
    private const double BottomMargin = 56;
    private const double MinSideMargin = 24;
    private bool _hasUserPositioned;
    private bool _scrollPending;
    private MainShellViewModel? _shell;

    public event EventHandler<string>? PromptSubmitted;

    public string PromptText { get; set; } = string.Empty;

    public ICommand CloseCommand { get; }

    public ComputerUseOverlayWindow(MainShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        InitializeComponent();
        DataContext = shell;
        _shell = shell;
        CloseCommand = new RelayCommand(_ => Close());
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        shell.Messages.CollectionChanged += OnMessagesCollectionChanged;
        AttachMessageListeners(shell);
    }

    public void FocusComposer()
    {
        Activate();
        PromptBox.Focus();
        Keyboard.Focus(PromptBox);
        PromptBox.CaretIndex = PromptBox.Text.Length;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionFloatingComposer();
        FocusComposer();
        ScheduleScrollTranscriptToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_shell is null)
        {
            return;
        }

        _shell.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        DetachMessageListeners(_shell);
        _shell = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || _hasUserPositioned)
        {
            return;
        }

        PositionFloatingComposer();
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_shell is null)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            DetachMessageListeners(_shell);
            AttachMessageListeners(_shell);
        }
        else
        {
            if (e.OldItems is not null)
            {
                foreach (var item in e.OldItems)
                {
                    if (item is ChatMessageViewModel message)
                    {
                        message.PropertyChanged -= OnTranscriptMessagePropertyChanged;
                    }
                }
            }

            if (e.NewItems is not null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is ChatMessageViewModel message)
                    {
                        message.PropertyChanged += OnTranscriptMessagePropertyChanged;
                    }
                }
            }
        }

        ScheduleScrollTranscriptToEnd();
    }

    private void AttachMessageListeners(MainShellViewModel shell)
    {
        foreach (var message in shell.Messages)
        {
            message.PropertyChanged += OnTranscriptMessagePropertyChanged;
        }
    }

    private void DetachMessageListeners(MainShellViewModel shell)
    {
        foreach (var message in shell.Messages)
        {
            message.PropertyChanged -= OnTranscriptMessagePropertyChanged;
        }
    }

    private void OnTranscriptMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessageViewModel.Content)
            or nameof(ChatMessageViewModel.IsComputerUseTranscriptVisible))
        {
            ScheduleScrollTranscriptToEnd();
        }
    }

    private void ScheduleScrollTranscriptToEnd()
    {
        if (_scrollPending)
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _scrollPending = false;
            TranscriptScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void SendButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainShellViewModel { IsComposerStopVisible: true })
        {
            return;
        }

        var text = PromptBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        PromptText = string.Empty;
        PromptBox.Text = string.Empty;
        PromptSubmitted?.Invoke(this, text);
    }

    private void PromptBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
        {
            SendButton_OnClick(sender, e);
            e.Handled = true;
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void DragHandle_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
            _hasUserPositioned = true;
        }
        catch (InvalidOperationException)
        {
            // The mouse button may have been released before WPF entered the move loop.
        }
    }

    private void PositionFloatingComposer()
    {
        if (!TryGetCursorWorkArea(out var workArea, out var dpiScale))
        {
            var fallback = SystemParameters.WorkArea;
            var fallbackWidth = Math.Min(
                DefaultWidth,
                Math.Max(420, fallback.Width - (MinSideMargin * 2)));
            Width = fallbackWidth;
            Left = fallback.Left + ((fallback.Width - fallbackWidth) / 2);
            Top = Math.Max(fallback.Top + 12, fallback.Bottom - ActualHeight - BottomMargin);
            return;
        }

        var availableWidthPixels = workArea.Right - workArea.Left;
        var targetWidthPixels = (int)Math.Round(Math.Min(
            DefaultWidth * dpiScale,
            Math.Max(420 * dpiScale, availableWidthPixels - (MinSideMargin * 2 * dpiScale))));
        Width = targetWidthPixels / dpiScale;

        var targetHeightPixels = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpiScale));
        var leftPixels = workArea.Left + ((availableWidthPixels - targetWidthPixels) / 2);
        var topPixels = Math.Max(
            workArea.Top + (int)Math.Round(12 * dpiScale),
            workArea.Bottom - targetHeightPixels - (int)Math.Round(BottomMargin * dpiScale));
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(
                handle,
                IntPtr.Zero,
                leftPixels,
                topPixels,
                targetWidthPixels,
                targetHeightPixels,
                SwpNoActivate | SwpNoZOrder);
        }
    }

    private static bool TryGetCursorWorkArea(out RectNative workArea, out double dpiScale)
    {
        workArea = default;
        dpiScale = 1;
        if (!GetCursorPos(out var point))
        {
            return false;
        }

        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfoEx();
        info.Size = Marshal.SizeOf<MonitorInfoEx>();
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        workArea = info.WorkArea;
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0)
            {
                dpiScale = Math.Max(1, dpiX / 96d);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return true;
    }

    private sealed class RelayCommand(Action<object?> execute) : ICommand
    {
        private readonly Action<object?> _execute = execute;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute(parameter);

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int Size;
        public RectNative Monitor;
        public RectNative WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
