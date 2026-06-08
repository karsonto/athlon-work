using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private const double AutoScrollBottomThreshold = 48;
    private bool _autoScrollEnabled = true;
    private bool _isProgrammaticScroll;
    private bool _shutdownInProgress;
    private bool _chatPointerDown;
    private bool _chatScrollLockedByUser;
    private ScrollViewer? _chatMessagesScrollViewer;
    private DispatcherTimer? _scrollThrottleTimer;
    private static readonly TimeSpan ScrollThrottleInterval = TimeSpan.FromMilliseconds(100);

    public MainWindow(MainWindowViewModel viewModel)
    {
        App.StartupTrace("MainWindow constructor entered");
        InitializeComponent();
        App.StartupTrace("MainWindow InitializeComponent completed");
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.ScrollChatToBottom = () => ScrollChatToEnd(immediate: false);
        _viewModel.ScrollChatToBottomImmediate = () => ScrollChatToEnd(immediate: true);
        _viewModel.ContextSidebarLayoutChanged += (_, _) => Dispatcher.Invoke(ApplyContextSidebarLayout);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        MarkdownMessageView.ContentInteractionChanged += (_, _) => UpdateChatScrollLock();
        Loaded += OnMainWindowLoaded;
        Closing += OnMainWindowClosing;
        StateChanged += (_, _) => UpdateMaximizeRestoreButton();
        UpdateMaximizeRestoreButton();
        App.StartupTrace("MainWindow DataContext assigned");
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyNavigationSidebarLayout();
        ApplyContextSidebarLayout();
        ApplyEditorPaneLayout();
        ApplyComposerLayout();
        AttachChatMessagesScrollViewer();
        ScrollChatToEnd(immediate: true);
    }

    private void AttachChatMessagesScrollViewer()
    {
        if (ChatMessagesList is null)
        {
            return;
        }

        _chatMessagesScrollViewer = FindVisualChild<ScrollViewer>(ChatMessagesList);
        if (_chatMessagesScrollViewer is null)
        {
            return;
        }

        _chatMessagesScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        _chatMessagesScrollViewer.ScrollChanged += ChatMessagesScrollViewer_OnScrollChanged;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownInProgress)
        {
            return;
        }

        if (!_viewModel.ConfirmCloseEditorTabs())
        {
            e.Cancel = true;
            return;
        }

        if (_viewModel.HasPendingShutdownWork)
        {
            var confirm = MessageBox.Show(
                this,
                "有对话正在生成或消息排队中，退出将停止所有任务。确定退出？",
                "退出 Athlon Agent",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        e.Cancel = true;
        ShutdownOverlay.Visibility = Visibility.Visible;
        IsEnabled = false;

        try
        {
            var progress = new Progress<string>(status =>
            {
                Dispatcher.Invoke(() => _viewModel.ShutdownStatusText = status);
            });
            await _viewModel.ShutdownAsync(progress).ConfigureAwait(true);
        }
        catch
        {
            // Proceed with exit even if cleanup fails.
        }

        _shutdownInProgress = true;
        Application.Current.Shutdown();
    }

    private void ApplyNavigationSidebarLayout()
    {
        if (NavigationSidebarColumn is null)
        {
            return;
        }

        NavigationSidebarColumn.MinWidth = MainWindowViewModel.NavigationSidebarMinWidth;
        NavigationSidebarColumn.MaxWidth = MainWindowViewModel.NavigationSidebarMaxWidth;
        NavigationSidebarColumn.Width = new GridLength(_viewModel.NavigationSidebarWidth);
    }

    private void NavigationSidebarSplitter_OnDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (NavigationSidebarColumn is null)
        {
            return;
        }

        var width = NavigationSidebarColumn.ActualWidth;
        if (width >= MainWindowViewModel.NavigationSidebarMinWidth)
        {
            _viewModel.UpdateNavigationSidebarWidth(width);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.HasChatMessages))
        {
            Dispatcher.Invoke(ApplyContextSidebarLayout);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.HasOpenEditorTabs))
        {
            Dispatcher.Invoke(ApplyEditorPaneLayout);
        }
    }

    private void ApplyEditorPaneLayout()
    {
        if (EditorPaneColumn is null || EditorPaneHost is null || EditorChatSplitter is null)
        {
            return;
        }

        if (!_viewModel.HasOpenEditorTabs)
        {
            EditorPaneColumn.MinWidth = 0;
            EditorPaneColumn.MaxWidth = double.PositiveInfinity;
            EditorPaneColumn.Width = new GridLength(0);
            EditorPaneHost.Visibility = Visibility.Collapsed;
            EditorChatSplitter.Visibility = Visibility.Collapsed;
            return;
        }

        EditorPaneColumn.MinWidth = MainWindowViewModel.EditorPaneMinWidth;
        EditorPaneColumn.MaxWidth = MainWindowViewModel.EditorPaneMaxWidth;
        EditorPaneColumn.Width = new GridLength(_viewModel.EditorPaneWidth);
        EditorPaneHost.Visibility = Visibility.Visible;
        EditorChatSplitter.Visibility = Visibility.Visible;
    }

    private void EditorChatSplitter_OnDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (EditorPaneColumn is null || !_viewModel.HasOpenEditorTabs)
        {
            return;
        }

        var width = EditorPaneColumn.ActualWidth;
        if (width >= MainWindowViewModel.EditorPaneMinWidth)
        {
            _viewModel.UpdateEditorPaneWidth(width);
        }
    }

    private void ApplyComposerLayout()
    {
        if (ComposerRow is null)
        {
            return;
        }

        ComposerRow.MinHeight = MainWindowViewModel.ComposerMinHeight;
        ComposerRow.MaxHeight = MainWindowViewModel.ComposerMaxHeight;
        ComposerRow.Height = new GridLength(_viewModel.ComposerHeight);
    }

    private void ComposerSplitter_OnDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (ComposerRow is null)
        {
            return;
        }

        var height = ComposerRow.ActualHeight;
        if (height >= MainWindowViewModel.ComposerMinHeight)
        {
            _viewModel.UpdateComposerHeight(height);
        }
    }

    private void ApplyContextSidebarLayout()
    {
        if (ContextSidebarColumn is null || ContextSidebarPanel is null || ContextSidebarSplitter is null)
        {
            return;
        }

        if (_viewModel.IsContextSidebarVisible)
        {
            ContextSidebarColumn.MinWidth = MainWindowViewModel.ContextSidebarMinWidth;
            ContextSidebarColumn.MaxWidth = MainWindowViewModel.ContextSidebarMaxWidth;
            ContextSidebarColumn.Width = new GridLength(_viewModel.ContextSidebarWidth);
            ContextSidebarPanel.Visibility = Visibility.Visible;
            ContextSidebarSplitter.Visibility = Visibility.Visible;
            if (ContextSidebarCollapsedRail is not null)
            {
                ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            ContextSidebarColumn.MinWidth = 0;
            ContextSidebarColumn.MaxWidth = double.PositiveInfinity;
            ContextSidebarColumn.Width = new GridLength(0);
            ContextSidebarPanel.Visibility = Visibility.Collapsed;
            ContextSidebarSplitter.Visibility = Visibility.Collapsed;
            if (ContextSidebarCollapsedRail is not null)
            {
                ContextSidebarCollapsedRail.Visibility = _viewModel.HasChatMessages
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }
    }

    private void ContextSidebarSplitter_OnDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!_viewModel.IsContextSidebarVisible || ContextSidebarColumn is null)
        {
            return;
        }

        var width = ContextSidebarColumn.ActualWidth;
        if (width < MainWindowViewModel.ContextSidebarCollapseDragThreshold)
        {
            _viewModel.SetContextSidebarVisible(false);
            _ = _viewModel.PersistUiLayoutForSidebarAsync();
            return;
        }

        if (width >= MainWindowViewModel.ContextSidebarMinWidth)
        {
            _viewModel.UpdateContextSidebarWidth(width);
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateMaximizeRestoreButton()
    {
        if (MaximizeRestoreButton is null)
        {
            return;
        }

        MaximizeRestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        MaximizeRestoreButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    private void ScrollChatToEnd(bool immediate)
    {
        if (_chatMessagesScrollViewer is null)
        {
            return;
        }

        if (!_autoScrollEnabled || ShouldSuppressChatAutoScroll())
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
            return;
        }

        _scrollThrottleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = ScrollThrottleInterval
        };
        _scrollThrottleTimer.Tick += OnScrollThrottleTimerTick;
        _scrollThrottleTimer.Start();
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
        if (_chatMessagesScrollViewer is null)
        {
            return;
        }

        if (!_autoScrollEnabled || ShouldSuppressChatAutoScroll())
        {
            return;
        }

        _chatMessagesScrollViewer.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                _isProgrammaticScroll = true;
                _chatMessagesScrollViewer!.ScrollToEnd();
                _isProgrammaticScroll = false;
            });
    }

    private void ChatMessagesScrollViewer_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _chatPointerDown = true;
    }

    private void ChatMessagesScrollViewer_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _chatPointerDown = false;
        UpdateChatScrollLock();
    }

    private void UpdateChatScrollLock()
    {
        _chatScrollLockedByUser = ChatScrollHelper.HasTextSelection(ChatMessagesList);
        if (_chatScrollLockedByUser)
        {
            _autoScrollEnabled = false;
            return;
        }

        if (_chatMessagesScrollViewer is not null && IsNearBottom(_chatMessagesScrollViewer))
        {
            _autoScrollEnabled = true;
        }
    }

    private bool ShouldSuppressChatAutoScroll() =>
        _chatPointerDown
        || _chatScrollLockedByUser
        || ChatScrollHelper.HasTextSelection(ChatMessagesList);

    private void ChatMessagesScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
        {
            return;
        }

        if (_isProgrammaticScroll)
        {
            return;
        }

        if (ShouldSuppressChatAutoScroll())
        {
            return;
        }

        _autoScrollEnabled = IsNearBottom(viewer);
    }

    private static bool IsNearBottom(ScrollViewer viewer)
    {
        var distanceFromBottom = viewer.ScrollableHeight - viewer.VerticalOffset;
        return distanceFromBottom <= AutoScrollBottomThreshold;
    }

    private void ApiKeyPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            _viewModel.ApiKey = passwordBox.Password;
        }
    }

    private void ComposerTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.IsAtCompletionOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    _viewModel.MoveAtCompletionSelection(1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    _viewModel.MoveAtCompletionSelection(-1);
                    e.Handled = true;
                    return;
                case Key.Tab:
                    AcceptAtCompletion();
                    e.Handled = true;
                    return;
                case Key.Enter:
                    AcceptAtCompletion();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    _viewModel.CloseAtCompletion();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            return;
        }

        if (_viewModel.SendCommand.CanExecute(null))
        {
            _viewModel.SendCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void ComposerTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        _viewModel.UpdateAtCompletion(textBox.Text, textBox.CaretIndex);
    }

    private void AtCompletionListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        AcceptAtCompletion();
        e.Handled = true;
    }

    private void AcceptAtCompletion()
    {
        if (!_viewModel.TryAcceptAtCompletion(ComposerTextBox.CaretIndex, out var newCaretIndex))
        {
            return;
        }

        ComposerTextBox.Focus();
        ComposerTextBox.CaretIndex = newCaretIndex;
    }

    private void WorkspaceTreeItem_OnExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: WorkspaceTreeNodeViewModel node })
        {
            return;
        }

        _viewModel.Sidebar.ExpandWorkspaceTreeNode(node);
    }

    private void WorkspaceTree_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var treeViewItem = FindAncestor<TreeViewItem>(source);
        if (treeViewItem?.DataContext is not WorkspaceTreeNodeViewModel node)
        {
            return;
        }

        if (node.IsPlaceholder || node.IsExpanderPlaceholder || node.IsDirectory || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        if (_viewModel.OpenWorkspaceTreeNodeInEditorCommand.CanExecute(node))
        {
            _viewModel.OpenWorkspaceTreeNodeInEditorCommand.Execute(node);
        }

        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T matched)
            {
                return matched;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}