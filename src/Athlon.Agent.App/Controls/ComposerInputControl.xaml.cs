using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Controls;

public partial class ComposerInputControl : UserControl
{
    private readonly ExecutedRoutedEventHandler _pasteHandler;
    private MainWindowViewModel? _viewModel;
    private bool _isReplayingPaste;

    public ComposerInputControl()
    {
        InitializeComponent();
        _pasteHandler = ComposerTextBox_OnPastePreviewExecuted;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => _viewModel = DataContext as MainWindowViewModel;
    }

    public ClipboardImageAttachmentReader? ClipboardImageReader { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel ??= DataContext as MainWindowViewModel;
        ComposerTextBox.AddHandler(
            CommandManager.PreviewExecutedEvent,
            _pasteHandler,
            handledEventsToo: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ComposerTextBox.RemoveHandler(CommandManager.PreviewExecutedEvent, _pasteHandler);
    }

    private void ComposerTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
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

    private async void ComposerTextBox_OnPastePreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste)
        {
            return;
        }

        if (_isReplayingPaste)
        {
            return;
        }

        if (_viewModel is null || ClipboardImageReader is null || !ClipboardImageReader.HasPotentialImages())
        {
            return;
        }

        e.Handled = true;
        if (!await TryPasteImagesFromClipboardAsync().ConfigureAwait(true))
        {
            try
            {
                _isReplayingPaste = true;
                ComposerTextBox.Paste();
            }
            finally
            {
                _isReplayingPaste = false;
            }
        }
    }

    private async Task<bool> TryPasteImagesFromClipboardAsync()
    {
        if (_viewModel is null || ClipboardImageReader is null)
        {
            return false;
        }

        var images = await ClipboardImageReader.TryReadImagesAsync().ConfigureAwait(true);
        if (images.Count == 0)
        {
            return false;
        }

        _viewModel.AddPendingImages(images);
        return true;
    }

    private void ComposerTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is null || sender is not TextBox textBox)
        {
            return;
        }

        _viewModel.UpdateComposerCompletion(textBox.Text, textBox.CaretIndex);
        if (_viewModel.IsAtCompletionOpen)
        {
            Dispatcher.BeginInvoke(SyncActiveCompletionListSelection, DispatcherPriority.Loaded);
        }
    }

    private void SyncActiveCompletionListSelection()
    {
        if (_viewModel?.IsAtCompletionOpen == true)
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
        if (_viewModel is null || !_viewModel.IsAtCompletionOpen || AtCompletionListBox.Items.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(_viewModel.SelectedAtCompletionIndex, 0, AtCompletionListBox.Items.Count - 1);
        AtCompletionListBox.SelectedIndex = index;
        AtCompletionListBox.ScrollIntoView(AtCompletionListBox.Items[index]);
    }

    private bool TryAcceptAtCompletion()
    {
        if (_viewModel is null)
        {
            return false;
        }

        if (!_viewModel.TryAcceptAtCompletion(ComposerTextBox.CaretIndex, out var newCaretIndex))
        {
            return false;
        }

        ComposerTextBox.Focus();
        ComposerTextBox.CaretIndex = newCaretIndex;
        return true;
    }
}
