using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Controls;

public partial class ComposerInputControl : UserControl
{
    private const double MinComposerTextHeight = 28;
    private const double MaxComposerTextHeight = 200;

    private readonly ExecutedRoutedEventHandler _pasteHandler;
    private MainShellViewModel? _viewModel;
    private bool _isReplayingPaste;
    private bool _isHandlingPaste;
    private bool _isAdjustingHeight;

    public ComposerInputControl()
    {
        InitializeComponent();
        _pasteHandler = ComposerTextBox_OnPastePreviewExecuted;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => AdjustComposerTextHeight();
        DataContextChanged += (_, _) =>
        {
            _viewModel = DataContext as MainShellViewModel;
            UpdatePlaceholderVisibility();
        };
        ComposerTextBox.GotFocus += (_, _) => UpdatePlaceholderVisibility();
        ComposerTextBox.LostFocus += (_, _) => UpdatePlaceholderVisibility();
    }

    public ClipboardImageAttachmentReader? ClipboardImageReader { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel ??= DataContext as MainShellViewModel;
        ComposerTextBox.AddHandler(
            CommandManager.PreviewExecutedEvent,
            _pasteHandler,
            handledEventsToo: true);
        UpdatePlaceholderVisibility();
        AdjustComposerTextHeight();
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

        if (e.Key == Key.V
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && TryBeginImagePaste(e))
        {
            _ = HandleImagePasteAsync();
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

        if (!TryBeginImagePaste(e))
        {
            return;
        }

        await HandleImagePasteAsync().ConfigureAwait(true);
    }

    private bool TryBeginImagePaste(RoutedEventArgs e)
    {
        if (_isReplayingPaste || _isHandlingPaste)
        {
            return false;
        }

        if (_viewModel is null || ClipboardImageReader is null || !ClipboardImageReader.HasPotentialImages())
        {
            return false;
        }

        e.Handled = true;
        return true;
    }

    private async Task HandleImagePasteAsync()
    {
        if (_isHandlingPaste)
        {
            return;
        }

        _isHandlingPaste = true;
        try
        {
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
        finally
        {
            _isHandlingPaste = false;
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
        UpdatePlaceholderVisibility();
        AdjustComposerTextHeight();

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

    private void AdjustComposerTextHeight()
    {
        if (_isAdjustingHeight || ComposerTextBox is null)
        {
            return;
        }

        _isAdjustingHeight = true;
        try
        {
            var width = ComposerTextBox.ActualWidth;
            if (width <= 1)
            {
                width = Math.Max(0, ActualWidth);
            }

            if (width <= 1)
            {
                return;
            }

            var desired = MeasureComposerTextHeight(width);
            var height = Math.Clamp(desired, MinComposerTextHeight, MaxComposerTextHeight);
            ComposerTextBox.Height = height;
            ComposerTextBox.VerticalScrollBarVisibility =
                desired > MaxComposerTextHeight + 0.5
                    ? ScrollBarVisibility.Auto
                    : ScrollBarVisibility.Disabled;
            ComposerTextBox.VerticalContentAlignment =
                height > MinComposerTextHeight + 4
                    ? VerticalAlignment.Top
                    : VerticalAlignment.Center;
        }
        finally
        {
            _isAdjustingHeight = false;
        }
    }

    private double MeasureComposerTextHeight(double availableWidth)
    {
        var text = ComposerTextBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return MinComposerTextHeight;
        }

        // Trailing newline does not advance FormattedText height by itself.
        var measureText = text.EndsWith('\n') || text.EndsWith('\r')
            ? text + " "
            : text;

        var contentWidth = Math.Max(
            availableWidth - ComposerTextBox.Padding.Left - ComposerTextBox.Padding.Right,
            1);

        var formatted = new FormattedText(
            measureText,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                ComposerTextBox.FontFamily,
                ComposerTextBox.FontStyle,
                ComposerTextBox.FontWeight,
                ComposerTextBox.FontStretch),
            ComposerTextBox.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(ComposerTextBox).PixelsPerDip)
        {
            MaxTextWidth = contentWidth,
            Trimming = TextTrimming.None
        };

        var chrome = ComposerTextBox.Padding.Top
            + ComposerTextBox.Padding.Bottom
            + ComposerTextBox.BorderThickness.Top
            + ComposerTextBox.BorderThickness.Bottom;

        return formatted.Height + chrome + 2;
    }

    private void UpdatePlaceholderVisibility()
    {
        var showPlaceholder = string.IsNullOrWhiteSpace(ComposerTextBox.Text)
            && !ComposerTextBox.IsKeyboardFocusWithin;
        PlaceholderText.Visibility = showPlaceholder ? Visibility.Visible : Visibility.Collapsed;
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
