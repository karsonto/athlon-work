using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private bool _isSyncingDocument;

    public ComposerInputControl()
    {
        InitializeComponent();
        _pasteHandler = ComposerTextBox_OnPastePreviewExecuted;
        ApplyPlaceholderText();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) =>
        {
            UpdateDocumentPageWidth();
            AdjustComposerTextHeight();
        };
        DataContextChanged += OnDataContextChanged;
        if (ComposerTextBox is not null)
        {
            ComposerTextBox.TextChanged += ComposerTextBox_OnTextChanged;
            ComposerTextBox.GotFocus += (_, _) => UpdatePlaceholderVisibility();
            ComposerTextBox.LostFocus += (_, _) => UpdatePlaceholderVisibility();
            DataObject.AddPastingHandler(ComposerTextBox, OnComposerPasting);
        }
    }

    public ClipboardImageAttachmentReader? ClipboardImageReader { get; set; }

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(
            nameof(Placeholder),
            typeof(string),
            typeof(ComposerInputControl),
            new PropertyMetadata(null, OnPlaceholderChanged));

    public string? Placeholder
    {
        get => (string?)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public void FocusInput()
    {
        if (ComposerTextBox is null)
        {
            return;
        }

        ComposerTextBox.Focus();
        Keyboard.Focus(ComposerTextBox);
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ComposerInputControl)d).ApplyPlaceholderText();

    private bool IsComposerVisualReady =>
        ComposerTextBox is not null && PlaceholderText is not null;

    private void ApplyPlaceholderText()
    {
        if (PlaceholderText is null)
        {
            return;
        }

        PlaceholderText.Text = string.IsNullOrEmpty(Placeholder)
            ? Localization.LocalizationHub.Instance["Chat_ComposerPlaceholder"]
            : Placeholder!;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainShellViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainShellViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncDocumentFromComposerText();
        }

        if (IsComposerVisualReady)
        {
            UpdatePlaceholderVisibility();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel ??= DataContext as MainShellViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (ComposerTextBox is not null)
        {
            ComposerTextBox.AddHandler(
                CommandManager.PreviewExecutedEvent,
                _pasteHandler,
                handledEventsToo: true);
        }

        UpdateDocumentPageWidth();
        SyncDocumentFromComposerText();
        UpdatePlaceholderVisibility();
        AdjustComposerTextHeight();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ComposerTextBox is not null)
        {
            ComposerTextBox.RemoveHandler(CommandManager.PreviewExecutedEvent, _pasteHandler);
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainShellViewModel.ComposerText))
        {
            SyncDocumentFromComposerText();
        }
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

    private static void OnComposerPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.FormatToApply = DataFormats.UnicodeText;
        }
    }

    private bool TryBeginImagePaste(RoutedEventArgs e)
    {
        if (_isReplayingPaste || _isHandlingPaste)
        {
            return false;
        }

        if (_viewModel is null || ClipboardImageReader is null || !ClipboardImageReader.HasPotentialPasteAttachments())
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

        var filePaths = ClipboardImageReader.GetClipboardFilePaths();
        if (filePaths.Length > 0)
        {
            await _viewModel.AddPendingFromFilePathsAsync(filePaths).ConfigureAwait(true);
            return true;
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
        if (!IsComposerVisualReady)
        {
            return;
        }

        UpdatePlaceholderVisibility();
        UpdateDocumentPageWidth();
        AdjustComposerTextHeight();

        if (_isSyncingDocument || _viewModel is null)
        {
            return;
        }

        var document = EnsureComposerDocument();
        if (document is null)
        {
            return;
        }

        var serialized = ComposerMentionDocument.Serialize(document);
        var caret = ComposerMentionDocument.GetSerializedOffset(
            document,
            ComposerTextBox.CaretPosition);
        _isSyncingDocument = true;
        try
        {
            if (!string.Equals(_viewModel.ComposerText, serialized, StringComparison.Ordinal))
            {
                _viewModel.ComposerText = serialized;
            }

            TryHydratePlainMentions(serialized, caret);
        }
        finally
        {
            _isSyncingDocument = false;
        }

        serialized = ComposerMentionDocument.Serialize(document);
        caret = ComposerMentionDocument.GetSerializedOffset(
            document,
            ComposerTextBox.CaretPosition);
        _viewModel.UpdateComposerCompletion(serialized, caret);
        if (_viewModel.IsAtCompletionOpen)
        {
            Dispatcher.BeginInvoke(SyncActiveCompletionListSelection, DispatcherPriority.Loaded);
        }
    }

    private void SyncDocumentFromComposerText()
    {
        if (_isSyncingDocument || _viewModel is null)
        {
            return;
        }

        var document = EnsureComposerDocument();
        if (document is null)
        {
            return;
        }

        var composerText = _viewModel.ComposerText ?? string.Empty;
        var serialized = ComposerMentionDocument.Serialize(document);
        if (string.Equals(serialized, composerText, StringComparison.Ordinal))
        {
            UpdatePlaceholderVisibility();
            AdjustComposerTextHeight();
            return;
        }

        var caret = Math.Clamp(
            ComposerMentionDocument.GetSerializedOffset(
                document,
                ComposerTextBox.CaretPosition),
            0,
            composerText.Length);
        _isSyncingDocument = true;
        try
        {
            ComposerTextBox.BeginChange();
            ComposerMentionDocument.Hydrate(document, composerText);
            ComposerTextBox.CaretPosition = ComposerMentionDocument.GetPointerAtOffset(
                document,
                caret);
            ComposerTextBox.EndChange();
        }
        finally
        {
            _isSyncingDocument = false;
        }

        UpdatePlaceholderVisibility();
        UpdateDocumentPageWidth();
        AdjustComposerTextHeight();
    }

    private void TryHydratePlainMentions(string serialized, int caret)
    {
        var document = EnsureComposerDocument();
        if (document is null)
        {
            return;
        }

        var excludeStart = -1;
        var excludeEnd = -1;
        if (ComposerCompletionQuery.TryGetAtQuerySpan(serialized, caret, out var atStart, out var atEnd))
        {
            excludeStart = atStart;
            excludeEnd = atEnd;
        }
        else if (ComposerCompletionQuery.TryGetDoubleSlashMentionSpan(
                     serialized,
                     caret,
                     out var slashStart,
                     out var slashEnd))
        {
            excludeStart = slashStart;
            excludeEnd = slashEnd;
        }

        var mentions = ComposerMentionDocument.ParseMentions(serialized, excludeStart, excludeEnd);
        if (mentions.Count == 0
            || mentions.Count == ComposerMentionDocument.CountChips(document))
        {
            return;
        }

        ComposerTextBox.BeginChange();
        ComposerMentionDocument.Hydrate(document, serialized, excludeStart, excludeEnd);
        ComposerTextBox.CaretPosition = ComposerMentionDocument.GetPointerAtOffset(
            document,
            caret);
        ComposerTextBox.EndChange();
    }

    private void UpdateDocumentPageWidth()
    {
        if (ComposerTextBox is null)
        {
            return;
        }

        var document = EnsureComposerDocument();
        if (document is null)
        {
            return;
        }

        var width = ComposerTextBox.ActualWidth
            - ComposerTextBox.Padding.Left
            - ComposerTextBox.Padding.Right
            - 4;
        if (width > 1)
        {
            document.PageWidth = width;
        }
    }

    private FlowDocument? EnsureComposerDocument()
    {
        if (ComposerTextBox is null)
        {
            return null;
        }

        if (ComposerTextBox.Document is { } existing)
        {
            return existing;
        }

        var document = new FlowDocument(new Paragraph { Margin = new Thickness(0) })
        {
            PagePadding = new Thickness(0)
        };
        ComposerTextBox.Document = document;
        return document;
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
            _viewModel?.SetComposerMultiLine(height > MinComposerTextHeight + 4);
        }
        finally
        {
            _isAdjustingHeight = false;
        }
    }

    private double MeasureComposerTextHeight(double availableWidth)
    {
        var document = EnsureComposerDocument();
        if (document is null)
        {
            return MinComposerTextHeight;
        }

        var serialized = ComposerMentionDocument.Serialize(document);
        if (string.IsNullOrEmpty(serialized)
            && ComposerMentionDocument.CountChips(document) == 0)
        {
            return MinComposerTextHeight;
        }

        var start = document.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var end = document.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        var documentHeight = Math.Max(end.Bottom - start.Top, 0);
        if (documentHeight > 1)
        {
            return documentHeight
                + ComposerTextBox.Padding.Top
                + ComposerTextBox.Padding.Bottom
                + 4;
        }

        var measureText = serialized.EndsWith('\n') || serialized.EndsWith('\r')
            ? serialized + " "
            : serialized;
        if (string.IsNullOrEmpty(measureText))
        {
            measureText = " ";
        }

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
        if (!IsComposerVisualReady)
        {
            return;
        }

        var document = EnsureComposerDocument();
        var serialized = document is null
            ? string.Empty
            : ComposerMentionDocument.Serialize(document);
        var showPlaceholder = string.IsNullOrWhiteSpace(serialized)
            && (document is null || ComposerMentionDocument.CountChips(document) == 0)
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

        var document = EnsureComposerDocument();
        if (document is null)
        {
            return false;
        }

        var caret = ComposerMentionDocument.GetSerializedOffset(
            document,
            ComposerTextBox.CaretPosition);
        if (!_viewModel.TryAcceptAtCompletion(caret, out var newCaretIndex))
        {
            return false;
        }

        ComposerTextBox.Focus();
        Dispatcher.BeginInvoke(
            () =>
            {
                var liveDocument = EnsureComposerDocument();
                if (liveDocument is null)
                {
                    return;
                }

                ComposerTextBox.CaretPosition = ComposerMentionDocument.GetPointerAtOffset(
                    liveDocument,
                    newCaretIndex);
            },
            DispatcherPriority.Loaded);
        return true;
    }
}
