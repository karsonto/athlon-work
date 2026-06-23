using System.ComponentModel;
using System.IO;
using System.Text.Json;
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
    private readonly AppUpdateService _updateService;
    private readonly ChatAutoScrollController _chatScroll;
    private readonly MainWindowLayoutBinder _layoutBinder;
    private bool _shutdownInProgress;

    public MainWindow(
        MainWindowViewModel viewModel,
        ClipboardImageAttachmentReader clipboardImageReader,
        AppUpdateService updateService)
    {
        App.StartupTrace("MainWindow constructor entered");
        InitializeComponent();
        App.StartupTrace("MainWindow InitializeComponent completed");
        _viewModel = viewModel;
        _clipboardImageReader = clipboardImageReader;
        _updateService = updateService;
        _chatScroll = new ChatAutoScrollController(Dispatcher, () => _viewModel.IsBusy);
        _layoutBinder = new MainWindowLayoutBinder(_viewModel, new MainWindowLayoutElements
        {
            NavigationSidebarColumn = NavigationSidebarColumn,
            EditorPaneColumn = EditorPaneColumn,
            ContextSidebarColumn = ContextSidebarColumn,
            ComposerRow = ComposerRow,
            EditorPaneHost = EditorPaneHost,
            EditorChatSplitter = EditorChatSplitter,
            ContextSidebarPanel = ContextSidebarPanel,
            ContextSidebarSplitter = ContextSidebarSplitter,
            ContextSidebarCollapsedRail = ContextSidebarCollapsedRail
        });
        DataContext = _viewModel;
        _viewModel.ScrollChatToBottom = () => _chatScroll.ScrollToEnd(immediate: false);
        _viewModel.ScrollChatToBottomImmediate = () => _chatScroll.ScrollToEnd(immediate: true);
        _viewModel.ContextSidebarLayoutChanged += (_, _) => Dispatcher.Invoke(_layoutBinder.ApplyContextSidebar);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        MarkdownMessageView.ContentInteractionChanged += (_, _) => _chatScroll.OnContentInteractionChanged();
        Loaded += OnMainWindowLoaded;
        Closing += OnMainWindowClosing;
        StateChanged += (_, _) => UpdateMaximizeRestoreButton();
        UpdateMaximizeRestoreButton();
        App.StartupTrace("MainWindow DataContext assigned");
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        _layoutBinder.ApplyAll();
        AttachComposerPasteHandler();
        if (ChatMessagesList is not null)
        {
            _chatScroll.Attach(ChatMessagesList);
        }

        _chatScroll.ScrollToEnd(immediate: true);
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

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        try
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Shutdown error: {ex}");
        }
    }

    private void NavigationSidebarSplitter_OnDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _layoutBinder.OnNavigationSidebarDragCompleted();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.HasChatMessages))
        {
            Dispatcher.Invoke(() =>
            {
                if (ChatMessagesList is not null)
                {
                    _chatScroll.Attach(ChatMessagesList);
                }

                _layoutBinder.ApplyContextSidebar();
                _chatScroll.OnHasChatMessagesChanged(_viewModel.HasChatMessages);
            });
        }

        if (e.PropertyName == nameof(MainWindowViewModel.HasOpenEditorTabs))
        {
            Dispatcher.Invoke(_layoutBinder.ApplyEditorPane);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsBusy))
        {
            Dispatcher.Invoke(() => _chatScroll.OnStreamingStateChanged(_viewModel.IsBusy));
        }
    }

    private void EditorChatSplitter_OnDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _layoutBinder.OnEditorPaneDragCompleted();

    private void ComposerSplitter_OnDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _layoutBinder.OnComposerDragCompleted();

    private void ContextSidebarSplitter_OnDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _layoutBinder.OnContextSidebarDragCompleted();

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
        var about = new Windows.AboutWindow(_updateService)
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

    private void ChatMessagesScrollViewer_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _chatScroll.HandlePreviewMouseLeftButtonDown();

    private void ChatMessagesScrollViewer_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _chatScroll.HandlePreviewMouseLeftButtonUp();

    private void ApiKeyPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            _viewModel.ApiKey = passwordBox.Password;
        }
    }

    private void KnowledgeEmbeddingApiKeyPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            _viewModel.KnowledgeEmbeddingApiKey = passwordBox.Password;
        }
    }

    private void KnowledgeTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is KnowledgeTreeNodeViewModel node)
        {
            _viewModel.KnowledgePageVm.SelectTreeNode(node);
        }
    }

    private void KnowledgeTree_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var treeViewItem = FindAncestor<TreeViewItem>(source);
        if (treeViewItem is null)
        {
            return;
        }

        treeViewItem.IsSelected = true;
        treeViewItem.Focus();
    }

    private void KnowledgeSaveModuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(this) is FrameworkElement focusedElement)
        {
            var bindingExpression = focusedElement.GetBindingExpression(TextBox.TextProperty);
            // #region agent log
            DebugLog("pre-fix", "H1", "MainWindow.KnowledgeSaveModuleButton_OnClick:focused-binding", new
            {
                focusedType = focusedElement.GetType().Name,
                hasTextBinding = bindingExpression is not null,
                focusedTextLength = focusedElement is TextBox textBox ? textBox.Text.Length : (int?)null
            });
            // #endregion
            focusedElement.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            return;
        }

        // #region agent log
        DebugLog("pre-fix", "H1", "MainWindow.KnowledgeSaveModuleButton_OnClick:no-focused-element", new
        {
            senderType = sender.GetType().Name
        });
        // #endregion
    }

    private void KnowledgeDocuments_OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            KnowledgeDragOverlay.Opacity = 1;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void KnowledgeDocuments_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void KnowledgeDocuments_OnDragLeave(object sender, DragEventArgs e)
    {
        KnowledgeDragOverlay.Opacity = 0;
        e.Handled = true;
    }

    private async void KnowledgeDocuments_OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            KnowledgeDragOverlay.Opacity = 0;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                await _viewModel.KnowledgePageVm.ImportDocumentsAsync(files);
            }

            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Drag-drop error: {ex}");
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
    }

    private void AtCompletionListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryAcceptAtCompletion();
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

    private static void DebugLog(string runId, string hypothesisId, string message, object data)
    {
        try
        {
            var payload = new
            {
                sessionId = "6740f2",
                id = Guid.NewGuid().ToString("N"),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                location = "MainWindow.xaml.cs",
                message,
                data,
                runId,
                hypothesisId
            };
            File.AppendAllText("F:/athlon-work/debug-6740f2.log", JsonSerializer.Serialize(payload) + Environment.NewLine);
        }
        catch
        {
            // Debug logging must never affect app behavior.
        }
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