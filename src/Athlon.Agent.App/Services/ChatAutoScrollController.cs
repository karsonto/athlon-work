using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Athlon.Agent.App.Services;

public sealed class ChatAutoScrollController : IDisposable
{
    private const double AutoScrollBottomThreshold = 48;
    private static readonly TimeSpan ScrollThrottleInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<bool> _isBusy;
    private readonly Dispatcher _dispatcher;
    private ListBox? _chatMessagesList;
    private ScrollViewer? _scrollViewer;
    private readonly DispatcherTimer _scrollThrottleTimer;
    private bool _autoScrollEnabled = true;
    private bool _isProgrammaticScroll;
    private bool _chatPointerDown;
    private bool _chatScrollLockedByUser;
    private bool _disposed;
    private ScrollChangedEventHandler? _scrollChangedHandler;

    public ChatAutoScrollController(Dispatcher dispatcher, Func<bool> isBusy)
    {
        _dispatcher = dispatcher;
        _isBusy = isBusy;
        _scrollThrottleTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = ScrollThrottleInterval
        };
        _scrollThrottleTimer.Tick += OnScrollThrottleTimerTick;
    }

    public void Attach(ListBox chatMessagesList)
    {
        _chatMessagesList = chatMessagesList;
        EnsureScrollViewer();
    }

    public void OnHasChatMessagesChanged(bool hasMessages)
    {
        EnsureScrollViewer();
        if (hasMessages)
        {
            _autoScrollEnabled = true;
            ScrollToEnd(immediate: true);
        }
    }

    public void OnStreamingStateChanged(bool isBusy)
    {
        if (isBusy)
        {
            _autoScrollEnabled = true;
            ScrollToEnd(immediate: true);
            return;
        }

        StopScrollThrottleTimer();
    }

    public void OnContentInteractionChanged()
    {
        StopScrollThrottleTimer();
        UpdateScrollLock();
    }

    public void ScrollToEnd(bool immediate)
    {
        EnsureScrollViewer();

        if (!_autoScrollEnabled || ShouldSuppressAutoScroll())
        {
            return;
        }

        if (immediate)
        {
            ExecuteScrollToEnd();
            return;
        }

        _scrollThrottleTimer.Stop();
        _scrollThrottleTimer.Start();
    }

    public void HandlePreviewMouseLeftButtonDown()
    {
        _chatPointerDown = true;
        StopScrollThrottleTimer();
    }

    public void HandlePreviewMouseLeftButtonUp()
    {
        _chatPointerDown = false;
        UpdateScrollLock();
    }

    public void HandleScrollChanged(ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer)
        {
            return;
        }

        if (_isProgrammaticScroll || ShouldSuppressAutoScroll())
        {
            return;
        }

        if (e.ExtentHeightChange > 0)
        {
            if (_autoScrollEnabled)
            {
                ExecuteScrollToEnd();
                return;
            }

            if (_isBusy() && IsNearBottom(viewer))
            {
                _autoScrollEnabled = true;
                ExecuteScrollToEnd();
                return;
            }
        }

        if (Math.Abs(e.VerticalChange) > 0.01)
        {
            _autoScrollEnabled = IsNearBottom(viewer);
        }
    }

    private void OnScrollThrottleTimerTick(object? sender, EventArgs e)
    {
        StopScrollThrottleTimer();
        ExecuteScrollToEnd();
    }

    private void StopScrollThrottleTimer()
    {
        _scrollThrottleTimer.Stop();
    }

    private void ExecuteScrollToEnd()
    {
        EnsureScrollViewer();

        if (!_autoScrollEnabled || ShouldSuppressAutoScroll() || _chatMessagesList is null)
        {
            return;
        }

        _chatMessagesList.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                if (!_autoScrollEnabled || ShouldSuppressAutoScroll())
                {
                    return;
                }

                _isProgrammaticScroll = true;
                ScrollListToBottom();

                _chatMessagesList!.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    () =>
                    {
                        if (_autoScrollEnabled && !ShouldSuppressAutoScroll())
                        {
                            ScrollListToBottom();
                        }

                        _isProgrammaticScroll = false;
                    });
            });
    }

    private void ScrollListToBottom()
    {
        EnsureScrollViewer();
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollToVerticalOffset(_scrollViewer.ScrollableHeight);
            return;
        }

        if (_chatMessagesList is { Items.Count: > 0 })
        {
            _chatMessagesList.ScrollIntoView(_chatMessagesList.Items[^1]);
        }
    }

    private void EnsureScrollViewer()
    {
        if (_chatMessagesList is null || _scrollViewer is not null)
        {
            return;
        }

        _chatMessagesList.ApplyTemplate();
        var scrollViewer = FindListBoxScrollViewer(_chatMessagesList);
        if (scrollViewer is null)
        {
            _chatMessagesList.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, EnsureScrollViewer);
            return;
        }

        _scrollViewer = scrollViewer;
        _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        _scrollChangedHandler = (_, e) => HandleScrollChanged(e);
        _scrollViewer.ScrollChanged += _scrollChangedHandler;
    }

    private void UpdateScrollLock()
    {
        _chatScrollLockedByUser = ChatScrollHelper.HasTextSelection(_chatMessagesList);
        if (_chatScrollLockedByUser)
        {
            _autoScrollEnabled = false;
            return;
        }

        if (_isBusy())
        {
            _autoScrollEnabled = true;
            return;
        }

        if (_scrollViewer is not null && IsNearBottom(_scrollViewer))
        {
            _autoScrollEnabled = true;
        }
    }

    private bool ShouldSuppressAutoScroll() =>
        _chatPointerDown || _chatScrollLockedByUser;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scrollThrottleTimer.Tick -= OnScrollThrottleTimerTick;
        _scrollThrottleTimer.Stop();

        if (_scrollViewer is not null && _scrollChangedHandler is not null)
        {
            _scrollViewer.ScrollChanged -= _scrollChangedHandler;
            _scrollChangedHandler = null;
            _scrollViewer = null;
        }
    }

    private static bool IsNearBottom(ScrollViewer viewer)
    {
        var distanceFromBottom = viewer.ScrollableHeight - viewer.VerticalOffset;
        return distanceFromBottom <= AutoScrollBottomThreshold;
    }

    private static ScrollViewer? FindListBoxScrollViewer(ListBox listBox)
    {
        if (listBox.Template?.FindName("ScrollViewer", listBox) is ScrollViewer named)
        {
            return named;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(listBox); i++)
        {
            var child = VisualTreeHelper.GetChild(listBox, i);
            if (child is ScrollViewer direct)
            {
                return direct;
            }

            for (var j = 0; j < VisualTreeHelper.GetChildrenCount(child); j++)
            {
                if (VisualTreeHelper.GetChild(child, j) is ScrollViewer nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
