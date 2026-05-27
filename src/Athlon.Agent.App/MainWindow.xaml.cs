using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        App.StartupTrace("MainWindow constructor entered");
        InitializeComponent();
        App.StartupTrace("MainWindow InitializeComponent completed");
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.ScrollChatToBottom = ScrollChatToEnd;
        Loaded += (_, _) => ScrollChatToEnd();
        App.StartupTrace("MainWindow DataContext assigned");
    }

    private void ScrollChatToEnd()
    {
        if (ChatMessagesScrollViewer is null)
        {
            return;
        }

        ChatMessagesScrollViewer.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => ChatMessagesScrollViewer.ScrollToEnd());
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
}