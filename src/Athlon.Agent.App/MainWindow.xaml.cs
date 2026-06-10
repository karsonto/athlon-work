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
    private readonly ClipboardImageAttachmentReader _clipboardImageReader;
    private const double AutoScrollBottomThreshold = 48;
    private bool _autoScrollEnabled = true;
    private bool _isProgrammaticScroll;
    private bool _shutdownInProgress;
    private bool _chatPointerDown;
    private bool _chatScrollLockedByUser;
    private ScrollViewer? _chatMessagesScrollViewer;
    private DispatcherTimer? _scrollThrottleTimer;
    private static readonly TimeSpan ScrollThrottleInterval = TimeSpan.FromMilliseconds(100);

    public MainWindow(MainWindowViewModel viewModel, ClipboardImageAttachmentReader clipboardImageReader)
    {
        App.StartupTrace("MainWindow constructor entered");
        InitializeComponent();
        App.StartupTrace("MainWindow InitializeComponent completed");
        _viewModel = viewModel;
        _clipboardImageReader = clipboardImageReader;
        DataContext = _viewModel;
        _viewModel.ScrollChatToBottom = () => ScrollChatToEnd(immediate: false);
        _viewModel.ScrollChatToBottomImmediate = () => ScrollChatToEnd(immediate: true);
        _viewModel.ContextSidebarLayoutChanged += (_, _) => Dispatcher.Invoke(ApplyContextSidebarLayout);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        MarkdownMessageView.ContentInteractionChanged += (_, _) =>
        {
            StopScrollThrottleTimer();
            UpdateChatScrollLock();
        };
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
        AttachComposerPasteHandler();
        AttachChatMessagesScrollViewer();
        ScrollChatToEnd(immediate: true);
    }

    private void AttachComposerPasteHandler()
    {
        if (ComposerTextBox is null)
        {
            return;
        }

        ComposerTextBox.AddHandler(
            CommandManager.PreviewExecutedEvent,
            new ExecutedRoutedEventHandler(ComposerTextBox_OnPastePreviewExecuted),
            handledEventsToo: true);
    }

    private void AttachChatMessagesScrollViewer() => EnsureChatMessagesScrollViewer();

    private void EnsureChatMessagesScrollViewer()
    {
        if (ChatMessagesList is null)
        {
            return;
        }

        if (_chatMessagesScrollViewer is not null)
        {
            return;
        }

        ChatMessagesList.ApplyTemplate();
        var scrollViewer = FindListBoxScrollViewer(ChatMessagesList);
        if (scrollViewer is null)
        {
            ChatMessagesList.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, EnsureChatMessagesScrollViewer);
            return;
        }

        _chatMessagesScrollViewer = scrollViewer;
        _chatMessagesScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        _chatMessagesScrollViewer.ScrollChanged += ChatMessagesScrollViewer_OnScrollChanged;
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
            Dispatcher.Invoke(() =>
            {
                EnsureChatMessagesScrollViewer();
                ApplyContextSidebarLayout();
                if (_viewModel.HasChatMessages)
                {
                    _autoScrollEnabled = true;
                    ScrollChatToEnd(immediate: true);
                }
            });
        }

        if (e.PropertyName == nameof(MainWindowViewModel.HasOpenEditorTabs))
        {
            Dispatcher.Invoke(ApplyEditorPaneLayout);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsBusy))
        {
            Dispatcher.Invoke(UpdateStreamingAutoScroll);
        }
    }

    private void UpdateStreamingAutoScroll()
    {
        if (_viewModel.IsBusy)
        {
            _autoScrollEnabled = true;
            if (ChatMessagesList is not null)
            {
                ChatMessagesList.LayoutUpdated -= ChatMessagesList_OnLayoutUpdatedWhileBusy;
                ChatMessagesList.LayoutUpdated += ChatMessagesList_OnLayoutUpdatedWhileBusy;
            }

            ScrollChatToEnd(immediate: true);
            return;
        }

        if (ChatMessagesList is not null)
        {
            ChatMessagesList.LayoutUpdated -= ChatMessagesList_OnLayoutUpdatedWhileBusy;
        }
    }

    private void ChatMessagesList_OnLayoutUpdatedWhileBusy(object? sender, EventArgs e)
    {
        if (!_viewModel.IsBusy || !_autoScrollEnabled || ShouldSuppressChatAutoScroll())
        {
            return;
        }

        ScrollChatToEnd(immediate: false);
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
                // No edge affordance without an active conversation; use the chat header toggle instead.
                ContextSidebarCollapsedRail.Visibility = Visibility.Collapsed;
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
        if (IsWithinTitleBarMenu(e.OriginalSource as DependencyObject))
        {
            return;
        }

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

    private static bool IsWithinTitleBarMenu(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Menu or MenuItem)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
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

    private void HelpAboutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var about = new Windows.AboutWindow
        {
            Owner = this
        };
        about.ShowDialog();
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
        EnsureChatMessagesScrollViewer();

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
            _scrollThrottleTimer.Stop();
            _scrollThrottleTimer.Start();
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
        EnsureChatMessagesScrollViewer();

        if (!_autoScrollEnabled || ShouldSuppressChatAutoScroll())
        {
            return;
        }

        if (ChatMessagesList is null)
        {
            return;
        }

        ChatMessagesList.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                if (!_autoScrollEnabled || ShouldSuppressChatAutoScroll())
                {
                    return;
                }

                _isProgrammaticScroll = true;
                ScrollChatListToBottom();

                ChatMessagesList.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    () =>
                    {
                        if (_autoScrollEnabled && !ShouldSuppressChatAutoScroll())
                        {
                            ScrollChatListToBottom();
                        }

                        _isProgrammaticScroll = false;
                    });
            });
    }

    private void ScrollChatListToBottom()
    {
        if (ChatMessagesList is null)
        {
            return;
        }

        if (ChatMessagesList.Items.Count > 0)
        {
            ChatMessagesList.UpdateLayout();
            ChatMessagesList.ScrollIntoView(ChatMessagesList.Items[^1]);
        }

        EnsureChatMessagesScrollViewer();
        if (_chatMessagesScrollViewer is null)
        {
            return;
        }

        _chatMessagesScrollViewer.UpdateLayout();
        _chatMessagesScrollViewer.ScrollToVerticalOffset(_chatMessagesScrollViewer.ScrollableHeight);
    }

    private void ChatMessagesScrollViewer_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _chatPointerDown = true;
        StopScrollThrottleTimer();
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

        if (_viewModel.IsBusy)
        {
            _autoScrollEnabled = true;
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

        // Content grew during streaming while the user was pinned to the bottom.
        if (e.ExtentHeightChange > 0)
        {
            if (_autoScrollEnabled)
            {
                ExecuteScrollToEnd();
                return;
            }

            if (_viewModel.IsBusy && IsNearBottom(viewer))
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
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (TryPasteImagesFromClipboard())
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
        {
            if (_viewModel.IsSlashCompletionOpen && TryAcceptSlashCompletion())
            {
                e.Handled = true;
                return;
            }

            _viewModel.CloseSlashCompletion();

            if (_viewModel.IsAtCompletionOpen && TryAcceptAtCompletion())
            {
                e.Handled = true;
                return;
            }

            _viewModel.CloseAtCompletion();

            if (_viewModel.SendCommand.CanExecute(null))
            {
                _viewModel.SendCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (_viewModel.IsSlashCompletionOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    _viewModel.MoveSlashCompletionSelection(1);
                    SyncSlashCompletionListSelection();
                    e.Handled = true;
                    return;
                case Key.Up:
                    _viewModel.MoveSlashCompletionSelection(-1);
                    SyncSlashCompletionListSelection();
                    e.Handled = true;
                    return;
                case Key.Tab:
                    TryAcceptSlashCompletion();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    _viewModel.CloseSlashCompletion();
                    e.Handled = true;
                    return;
            }
        }

        if (_viewModel.IsAtCompletionOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    _viewModel.MoveAtCompletionSelection(1);
                    SyncAtCompletionListSelection();
                    e.Handled = true;
                    return;
                case Key.Up:
                    _viewModel.MoveAtCompletionSelection(-1);
                    SyncAtCompletionListSelection();
                    e.Handled = true;
                    return;
                case Key.Tab:
                    TryAcceptAtCompletion();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    _viewModel.CloseAtCompletion();
                    e.Handled = true;
                    return;
            }
        }
    }

    private void ComposerTextBox_OnPastePreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste)
        {
            return;
        }

        if (TryPasteImagesFromClipboard())
        {
            e.Handled = true;
        }
    }

    private bool TryPasteImagesFromClipboard()
    {
        var images = _clipboardImageReader.TryReadImages();
        if (images.Count == 0)
        {
            return false;
        }

        _viewModel.AddPendingImages(images);
        return true;
    }

    private void ComposerTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        _viewModel.UpdateComposerCompletion(textBox.Text, textBox.CaretIndex);
        Dispatcher.BeginInvoke(SyncActiveCompletionListSelection, DispatcherPriority.Loaded);
    }

    private void SyncActiveCompletionListSelection()
    {
        if (_viewModel.IsAtCompletionOpen)
        {
            SyncAtCompletionListSelection();
        }
        else if (_viewModel.IsSlashCompletionOpen)
        {
            SyncSlashCompletionListSelection();
        }
    }

    private void AtCompletionListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryAcceptAtCompletion();
        e.Handled = true;
    }

    private void SlashCompletionListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryAcceptSlashCompletion();
        e.Handled = true;
    }

    private void SyncAtCompletionListSelection()
    {
        if (!_viewModel.IsAtCompletionOpen || AtCompletionListBox.Items.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(_viewModel.SelectedAtCompletionIndex, 0, AtCompletionListBox.Items.Count - 1);
        AtCompletionListBox.SelectedIndex = index;
        AtCompletionListBox.ScrollIntoView(AtCompletionListBox.Items[index]);
    }

    private void SyncSlashCompletionListSelection()
    {
        if (!_viewModel.IsSlashCompletionOpen || SlashCompletionListBox.Items.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(_viewModel.SelectedSlashCompletionIndex, 0, SlashCompletionListBox.Items.Count - 1);
        SlashCompletionListBox.SelectedIndex = index;
        SlashCompletionListBox.ScrollIntoView(SlashCompletionListBox.Items[index]);
    }

    private bool TryAcceptSlashCompletion()
    {
        if (!_viewModel.TryAcceptSlashCompletion(ComposerTextBox.CaretIndex, out var newCaretIndex))
        {
            return false;
        }

        ComposerTextBox.Focus();
        ComposerTextBox.CaretIndex = newCaretIndex;
        return true;
    }

    private bool TryAcceptAtCompletion()
    {
        if (!_viewModel.TryAcceptAtCompletion(ComposerTextBox.CaretIndex, out var newCaretIndex))
        {
            return false;
        }

        ComposerTextBox.Focus();
        ComposerTextBox.CaretIndex = newCaretIndex;
        return true;
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