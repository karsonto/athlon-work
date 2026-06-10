using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Athlon.Agent.App.Services;

public sealed class ChatAutoScrollController
{
    private const double AutoScrollBottomThreshold = 48;
    private static readonly TimeSpan ScrollThrottleInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<bool> _isBusy;
    private readonly Dispatcher _dispatcher;
    private ListBox? _chatMessagesList;
    private ScrollViewer? _scrollViewer;
    private DispatcherTimer? _scrollThrottleTimer;
    private bool _autoScrollEnabled = true;
    private bool _isProgrammaticScroll;
    private bool _chatPointerDown;
    private bool _chatScrollLockedByUser;

    public ChatAutoScrollController(Dispatcher dispatcher, Func<bool> isBusy)
    {
        _dispatcher = dispatcher;
        _isBusy = isBusy;
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
            if (_chatMessagesList is not null)
            {
                _chatMessagesList.LayoutUpdated -= OnLayoutUpdatedWhileBusy;
                _chatMessagesList.LayoutUpdated += OnLayoutUpdatedWhileBusy;
            }

            ScrollToEnd(immediate: true);
            return;
        }

        if (_chatMessagesList is not null)
        {
            _chatMessagesList.LayoutUpdated -= OnLayoutUpdatedWhileBusy;
        }
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

        if (_scrollThrottleTimer is not null)
        {
            _scrollThrottleTimer.Stop();
            _scrollThrottleTimer.Start();
            return;
        }

        _scrollThrottleTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = ScrollThrottleInterval
        };
        _scrollThrottleTimer.Tick += OnScrollThrottleTimerTick;
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

    private void OnLayoutUpdatedWhileBusy(object? sender, EventArgs e)
    {
        if (!_isBusy() || !_autoScrollEnabled || ShouldSuppressAutoScroll())
        {
            return;
        }

        ScrollToEnd(immediate: false);
    }

    private void OnScrollThrottleTimerTick(object? sender, EventArgs e)
    {
        StopScrollThrottleTimer();
        ExecuteScrollToEnd();
    }

    private void StopScrollThrottleTimer()
    {
        if (_scrollThrottleTimer is null)
        {
            return;
        }

        _scrollThrottleTimer.Stop();
        _scrollThrottleTimer.Tick -= OnScrollThrottleTimerTick;
        _scrollThrottleTimer = null;
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
        if (_chatMessagesList is null)
        {
            return;
        }

        if (_chatMessagesList.Items.Count > 0)
        {
            _chatMessagesList.UpdateLayout();
            _chatMessagesList.ScrollIntoView(_chatMessagesList.Items[^1]);
        }

        EnsureScrollViewer();
        if (_scrollViewer is null)
        {
            return;
        }

        _scrollViewer.UpdateLayout();
        _scrollViewer.ScrollToVerticalOffset(_scrollViewer.ScrollableHeight);
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
        _scrollViewer.ScrollChanged += (_, e) => HandleScrollChanged(e);
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
        _chatPointerDown
        || _chatScrollLockedByUser
        || ChatScrollHelper.HasTextSelection(_chatMessagesList);

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
