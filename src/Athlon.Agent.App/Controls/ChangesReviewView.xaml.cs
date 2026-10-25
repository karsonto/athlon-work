using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Controls;

public partial class ChangesReviewView : UserControl
{
    public static readonly DependencyProperty ReviewViewportWidthProperty = DependencyProperty.Register(
        nameof(ReviewViewportWidth),
        typeof(double),
        typeof(ChangesReviewView),
        new PropertyMetadata(double.NaN));

    private ChangesWorkspaceTabViewModel? _viewModel;
    private ScrollViewer? _innerScroller;
    private double _savedOffset;
    private bool _restoreOffset;

    public double ReviewViewportWidth
    {
        get => (double)GetValue(ReviewViewportWidthProperty);
        set => SetValue(ReviewViewportWidthProperty, value);
    }

    public ChangesReviewView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
        Loaded += OnLoaded;
        ReviewScroller.SizeChanged += OnScrollerChanged;
        ReviewScroller.ScrollChanged += OnScrollerScrollChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureInnerScroller();
        UpdateViewportWidth();
        if (_viewModel?.PendingScrollPath is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, ApplyScroll);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_innerScroller is not null)
        {
            _innerScroller.ScrollChanged -= OnInnerScrollChanged;
            _innerScroller = null;
        }

        Attach(null);
    }

    private void OnScrollerChanged(object sender, SizeChangedEventArgs e) => UpdateViewportWidth();

    private void OnScrollerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.ViewportWidthChange) > 0.5)
        {
            UpdateViewportWidth();
        }
    }

    private void UpdateViewportWidth()
    {
        var outer = ReviewScroller.ViewportWidth;
        if (outer <= 0)
        {
            return;
        }

        EnsureInnerScroller();
        var available = outer;
        if (_innerScroller is not null
            && _innerScroller.ViewportWidth > 0
            && ReviewList.ActualWidth <= outer + 1)
        {
            // The list fills the window, so its vertical scrollbar sits on the visible right edge.
            available = Math.Min(outer, _innerScroller.ViewportWidth);
        }

        ReviewViewportWidth = available;
    }

    private void EnsureInnerScroller()
    {
        var inner = FindScrollViewer(ReviewList);
        if (ReferenceEquals(inner, _innerScroller))
        {
            return;
        }

        if (_innerScroller is not null)
        {
            _innerScroller.ScrollChanged -= OnInnerScrollChanged;
        }

        _innerScroller = inner;
        if (_innerScroller is not null)
        {
            _innerScroller.ScrollChanged += OnInnerScrollChanged;
        }
    }

    private void OnInnerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.ViewportWidthChange) > 0.5)
        {
            UpdateViewportWidth();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Attach(e.NewValue as ChangesWorkspaceTabViewModel);

    private void Attach(ChangesWorkspaceTabViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.RowsChanging -= OnRowsChanging;
            _viewModel.RowsChanged -= OnRowsChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.RowsChanging += OnRowsChanging;
        _viewModel.RowsChanged += OnRowsChanged;
        if (_viewModel.PendingScrollPath is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, ApplyScroll);
        }
    }

    private void OnRowsChanging(object? sender, EventArgs e)
    {
        _savedOffset = FindScrollViewer(ReviewList)?.VerticalOffset ?? 0;
        _restoreOffset = _viewModel?.PendingScrollPath is null;
    }

    private void OnRowsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            UpdateViewportWidth();
            ApplyScroll();
        });

    private void ApplyScroll()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.PendingScrollPath is string path)
        {
            var row = _viewModel.Rows.OfType<ChangesFileHeaderRowViewModel>().FirstOrDefault(item =>
                string.Equals(item.RelativePath, path, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                ReviewList.ScrollIntoView(row);
            }

            _viewModel.ClearPendingScroll();
            _restoreOffset = false;
            return;
        }

        if (!_restoreOffset)
        {
            return;
        }

        FindScrollViewer(ReviewList)?.ScrollToVerticalOffset(_savedOffset);
        _restoreOffset = false;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            var nested = FindScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
