using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private const double AutoScrollBottomThreshold = 48;
    private bool _autoScrollEnabled = true;
    private bool _isProgrammaticScroll;

    public MainWindow(MainWindowViewModel viewModel)
    {
        App.StartupTrace("MainWindow constructor entered");
        InitializeComponent();
        App.StartupTrace("MainWindow InitializeComponent completed");
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.ScrollChatToBottom = ScrollChatToEnd;
        Loaded += (_, _) => ScrollChatToEnd();
        StateChanged += (_, _) => UpdateMaximizeRestoreButton();
        UpdateMaximizeRestoreButton();
        App.StartupTrace("MainWindow DataContext assigned");
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

    private void ScrollChatToEnd()
    {
        if (ChatMessagesScrollViewer is null)
        {
            return;
        }

        if (!_autoScrollEnabled)
        {
            return;
        }

        ChatMessagesScrollViewer.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                _isProgrammaticScroll = true;
                ChatMessagesScrollViewer.ScrollToEnd();
                _isProgrammaticScroll = false;
            });
    }

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

        if (node.IsPlaceholder || node.IsDirectory || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        _viewModel.OpenWorkspaceFile(node.FullPath);
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