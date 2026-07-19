using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.Services.Speech;
using Athlon.Agent.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ChatPageViewModel : ObservableObject
{
    private readonly ComposerCoordinator _composer;
    private readonly SessionTurnCoordinator _sessionTurns;
    private readonly IImageAttachmentReader _imageAttachmentReader;
    private readonly IChatDocumentAttachmentExtractor _documentExtractor;
    private readonly IChatScrollService _chatScroll;
    private readonly ISpeechToTextService _speechToText;
    private readonly ILocalizationService _loc;

    private Func<string>? _getDisplayedSessionId;
    private Func<AgentSession>? _getSession;
    private Func<SessionTurnUiController>? _getActiveUi;
    private Action<string>? _setSettingsStatus;
    private Action? _notifyCommandStatesChanged;
    private Action? _syncWorkspaceContext;
    private Action<bool>? _setIsBusy;
    private Func<IReadOnlyList<string>>? _getIgnorePatterns;
    private Func<bool>? _tryCancelCompaction;
    private Func<ComposerSlashCommandContext>? _createSlashCommandContext;

    public ChatPageViewModel(
        ComposerCoordinator composer,
        SessionTurnCoordinator sessionTurns,
        IImageAttachmentReader imageAttachmentReader,
        IChatDocumentAttachmentExtractor documentExtractor,
        IChatScrollService chatScroll,
        ISpeechToTextService speechToText,
        ILocalizationService localization)
    {
        _composer = composer;
        _sessionTurns = sessionTurns;
        _imageAttachmentReader = imageAttachmentReader;
        _documentExtractor = documentExtractor;
        _chatScroll = chatScroll;
        _speechToText = speechToText;
        _loc = localization;

        _speechToText.AvailabilityChanged += OnSpeechAvailabilityChanged;
        _speechToText.FinalText += OnSpeechFinalText;
        AppCultureManager.CultureChanged += OnCultureChanged;

        _ = InitializeSpeechAsync();
    }

    public void Configure(
        Func<string> getDisplayedSessionId,
        Func<AgentSession> getSession,
        Func<SessionTurnUiController> getActiveUi,
        Action<string> setSettingsStatus,
        Action notifyCommandStatesChanged,
        Action syncWorkspaceContext,
        Action<bool> setIsBusy,
        Func<IReadOnlyList<string>> getIgnorePatterns,
        Func<bool> tryCancelCompaction,
        Func<ComposerSlashCommandContext> createSlashCommandContext)
    {
        _getDisplayedSessionId = getDisplayedSessionId;
        _getSession = getSession;
        _getActiveUi = getActiveUi;
        _setSettingsStatus = setSettingsStatus;
        _notifyCommandStatesChanged = notifyCommandStatesChanged;
        _syncWorkspaceContext = syncWorkspaceContext;
        _setIsBusy = setIsBusy;
        _getIgnorePatterns = getIgnorePatterns;
        _tryCancelCompaction = tryCancelCompaction;
        _createSlashCommandContext = createSlashCommandContext;
    }

    [ObservableProperty]
    private string composerText = string.Empty;

    [ObservableProperty]
    private bool isAtCompletionOpen;

    [ObservableProperty]
    private int selectedAtCompletionIndex = -1;

    [ObservableProperty]
    private bool isReadingAttachments;

    public ObservableCollection<AtCompletionItemViewModel> AtCompletionItems { get; } = new();
    public ObservableCollection<PendingImageAttachmentViewModel> PendingImageAttachments { get; } = new();
    public ObservableCollection<PendingDocumentAttachmentViewModel> PendingDocumentAttachments { get; } = new();
    public bool HasPendingImages => PendingImageAttachments.Count > 0;
    public bool HasPendingDocuments => PendingDocumentAttachments.Count > 0;
    public bool HasPendingAttachments => HasPendingImages || HasPendingDocuments;
    public bool IsComposerEmpty => string.IsNullOrWhiteSpace(ComposerText);

    public bool IsSpeechInputAvailable => _speechToText.IsAvailable;

    [ObservableProperty]
    private bool isSpeechListening;

    public string SendButtonToolTip => IsSpeechListening
        ? _loc["Chat_SpeechListeningTooltip"]
        : IsSpeechInputAvailable
            ? _loc["Chat_SendTooltipWithSpeech"]
            : _loc["Chat_SendTooltip"];

    public string SendButtonGlyph => IsSpeechListening ? "🎤" : "➤";

    public async Task StartSpeechInputAsync()
    {
        if (!IsSpeechInputAvailable)
        {
            return;
        }

        // Arm UI first so the glyph flips even if the recognizer start is slow/fails.
        if (!IsSpeechListening)
        {
            IsSpeechListening = true;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
        }

        if (_speechToText.IsListening)
        {
            return;
        }

        await _speechToText.StartListeningAsync().ConfigureAwait(true);
        // Keep IsSpeechListening true until StopSpeechInputAsync so the glyph stays while held.
    }

    public async Task StopSpeechInputAsync()
    {
        if (!IsSpeechListening && !_speechToText.IsListening)
        {
            return;
        }

        try
        {
            await _speechToText.StopListeningAsync().ConfigureAwait(true);
        }
        finally
        {
            IsSpeechListening = false;
        }
    }

    public void OnPendingImagesChanged()
    {
        OnPropertyChanged(nameof(HasPendingImages));
        OnPropertyChanged(nameof(HasPendingAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    public void OnPendingDocumentsChanged()
    {
        OnPropertyChanged(nameof(HasPendingDocuments));
        OnPropertyChanged(nameof(HasPendingAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SelectImagesAsync() => await SelectAttachmentsAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task SelectAttachmentsAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("Chat_SelectFiles"),
            Multiselect = true,
            Filter = Strings.Get("Chat_SelectFilesFilter")
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        await AddPendingFromFilePathsAsync(dialog.FileNames).ConfigureAwait(true);
    }

    public async Task AddPendingFromFilePathsAsync(IEnumerable<string> filePaths)
    {
        var paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var imagePaths = new List<string>();
        var rejected = new List<string>();

        foreach (var path in paths)
        {
            if (_documentExtractor.IsLegacyPresentation(path))
            {
                rejected.Add(Strings.Format("Chat_LegacyPptRejected", Path.GetFileName(path)));
                continue;
            }

            if (_documentExtractor.IsImageFile(path))
            {
                imagePaths.Add(path);
                continue;
            }

            if (_documentExtractor.IsSupportedDocument(path))
            {
                AddPendingDocument(path);
                continue;
            }

            rejected.Add(Strings.Format("Chat_UnsupportedAttachment", Path.GetFileName(path)));
        }

        if (imagePaths.Count > 0)
        {
            var images = await _imageAttachmentReader.ReadImagesAsync(imagePaths).ConfigureAwait(true);
            AddPendingImages(images);
        }

        if (rejected.Count > 0)
        {
            _setSettingsStatus?.Invoke(rejected[0]);
        }
    }

    private void AddPendingDocument(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (PendingDocumentAttachments.Any(item =>
                string.Equals(item.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        PendingDocumentAttachments.Add(new PendingDocumentAttachmentViewModel(fullPath));
        OnPendingDocumentsChanged();
    }

    public void AddPendingImages(IEnumerable<ImageAttachment> images) =>
        _composer.AddPendingImages(images, PendingImageAttachments);

    [RelayCommand]
    private void RemovePendingImage(PendingImageAttachmentViewModel? image)
    {
        if (image is null)
        {
            return;
        }

        PendingImageAttachments.Remove(image);
    }

    [RelayCommand]
    private void RemovePendingDocument(PendingDocumentAttachmentViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        PendingDocumentAttachments.Remove(document);
        OnPendingDocumentsChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        CloseAtCompletion();

        if (IsReadingAttachments)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ComposerText)
            && PendingImageAttachments.Count == 0
            && PendingDocumentAttachments.Count == 0)
        {
            return;
        }

        if (await TryExecuteSlashCommandAsync().ConfigureAwait(true))
        {
            return;
        }

        var displayedSessionId = _getDisplayedSessionId!();
        var session = _getSession!();
        _sessionTurns.ReloadSkills();

        var pendingDocs = PendingDocumentAttachments.ToArray();
        var extractionResults = new List<ChatDocumentExtractionResult>();
        var visualAttachments = new List<ImageAttachment>();

        if (pendingDocs.Length > 0)
        {
            IsReadingAttachments = true;
            SendCommand.NotifyCanExecuteChanged();
            try
            {
                var failures = new List<string>();
                foreach (var document in pendingDocs)
                {
                    try
                    {
                        var extracted = await _documentExtractor
                            .ExtractAllVisualAsync(document.FilePath)
                            .ConfigureAwait(true);
                        extractionResults.Add(extracted);
                        visualAttachments.AddRange(extracted.VisualAttachments);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{document.FileName}: {ex.Message}");
                    }
                }

                if (extractionResults.Count == 0)
                {
                    _setSettingsStatus?.Invoke(failures.Count > 0
                        ? failures[0]
                        : Strings.Get("Chat_AttachmentParseFailed"));
                    return;
                }

                if (failures.Count > 0)
                {
                    _setSettingsStatus?.Invoke(
                        Strings.Format("Chat_AttachmentParsePartial", extractionResults.Count, failures.Count));
                }
            }
            finally
            {
                IsReadingAttachments = false;
                SendCommand.NotifyCanExecuteChanged();
            }
        }

        var expandedComposer = _sessionTurns.ExpandComposerInput(ComposerText);
        var input = ChatDocumentAttachmentFormatter.JoinUserInputWithExtractedDocuments(
            expandedComposer,
            extractionResults);

        foreach (var visual in visualAttachments)
        {
            AddPendingImages([visual]);
        }

        var imageAttachments = _composer.PersistPendingImages(displayedSessionId, PendingImageAttachments);
        ComposerText = string.Empty;
        _syncWorkspaceContext!();

        var ui = _sessionTurns.GetOrCreateUi(displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        PendingImageAttachments.Clear();
        PendingDocumentAttachments.Clear();
        OnPendingDocumentsChanged();

        if (_sessionTurns.IsRunning(displayedSessionId))
        {
            _sessionTurns.EnqueueTurn(displayedSessionId, input, imageAttachments, ui);
            _setSettingsStatus!(Strings.Get("Chat_QueuedStatus"));
            _notifyCommandStatesChanged!();
            return;
        }

        ui.AddUserMessage(input, imageAttachments);
        var error = _sessionTurns.TryStartTurn(displayedSessionId, session, input, imageAttachments, ui);
        if (error is not null)
        {
            _setSettingsStatus!(error);
            _notifyCommandStatesChanged!();
            return;
        }

        UpdateDisplayedBusyState();
        _notifyCommandStatesChanged!();
    }

    [RelayCommand]
    private void RemoveQueuedTurn(QueuedTurnViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _sessionTurns.QueuedTurnPresenter.Remove(_getDisplayedSessionId!(), item.QueueId);
    }

    [RelayCommand]
    private void Stop()
    {
        if (_tryCancelCompaction?.Invoke() == true)
        {
            return;
        }

        var sessionId = _getDisplayedSessionId!();
        _sessionTurns.TurnHost.Cancel(sessionId);
        _sessionTurns.QueuedTurnPresenter.Clear(sessionId);
        UpdateDisplayedBusyState();
    }

    public void UpdateDisplayedBusyState() =>
        _setIsBusy!(_sessionTurns.TurnHost.IsRunning(_getDisplayedSessionId!()));

    public void UpdateComposerCompletion(string composerText, int caretIndex) =>
        UpdateAtCompletion(composerText, caretIndex);

    public void UpdateAtCompletion(string composerText, int caretIndex) =>
        _composer.UpdateAtCompletion(
            composerText,
            caretIndex,
            _getSession!().ActiveWorkspace,
            _getIgnorePatterns!(),
            AtCompletionItems,
            open => IsAtCompletionOpen = open,
            index => SelectedAtCompletionIndex = index,
            SelectedAtCompletionIndex);

    public void MoveAtCompletionSelection(int delta) =>
        _composer.MoveSelection(
            delta,
            IsAtCompletionOpen,
            AtCompletionItems.Count,
            SelectedAtCompletionIndex,
            index => SelectedAtCompletionIndex = index);

    public bool TryAcceptAtCompletion(int caretIndex, out int newCaretIndex) =>
        _composer.TryAcceptAtCompletion(
            ComposerText,
            caretIndex,
            IsAtCompletionOpen,
            SelectedAtCompletionIndex,
            AtCompletionItems,
            text => ComposerText = text,
            CloseAtCompletion,
            _createSlashCommandContext?.Invoke(),
            out newCaretIndex);

    public async Task<bool> TryExecuteSlashCommandAsync()
    {
        var context = _createSlashCommandContext?.Invoke();
        if (context is null || string.IsNullOrWhiteSpace(ComposerText))
        {
            return false;
        }

        return await _composer.TryExecuteSlashCommandAsync(
            ComposerText,
            context,
            text => ComposerText = text).ConfigureAwait(true);
    }

    public void CloseAtCompletion() =>
        _composer.CloseAtCompletion(
            AtCompletionItems,
            _ => IsAtCompletionOpen = false,
            index => SelectedAtCompletionIndex = index);

    private void RequestScrollToBottom() => _chatScroll.ScrollToBottom();

    private void RequestScrollToBottomImmediate() => _chatScroll.ScrollToBottomImmediate();

    private bool CanSend() =>
        !IsReadingAttachments
        && (!string.IsNullOrWhiteSpace(ComposerText)
            || PendingImageAttachments.Count > 0
            || PendingDocumentAttachments.Count > 0);

    partial void OnComposerTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsComposerEmpty));
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsReadingAttachmentsChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    private async Task InitializeSpeechAsync()
    {
        try
        {
            await _speechToText.ProbeAvailabilityAsync().ConfigureAwait(true);
        }
        catch
        {
            // Probe failures stay silent; long-press speech stays disabled.
        }

        OnPropertyChanged(nameof(IsSpeechInputAvailable));
        OnPropertyChanged(nameof(SendButtonToolTip));
        OnPropertyChanged(nameof(SendButtonGlyph));
    }

    private void OnSpeechAvailabilityChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            OnPropertyChanged(nameof(IsSpeechInputAvailable));
            OnPropertyChanged(nameof(SendButtonToolTip));
            OnPropertyChanged(nameof(SendButtonGlyph));
            return;
        }

        dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(IsSpeechInputAvailable));
            OnPropertyChanged(nameof(SendButtonToolTip));
            OnPropertyChanged(nameof(SendButtonGlyph));
        });
    }

    private void OnSpeechFinalText(object? sender, string text)
    {
        void Apply()
        {
            ComposerText = ComposerSpeechText.AppendTranscript(ComposerText, text);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        dispatcher.Invoke(Apply);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SendButtonToolTip));
        OnPropertyChanged(nameof(SendButtonGlyph));
    }

    partial void OnIsSpeechListeningChanged(bool value)
    {
        OnPropertyChanged(nameof(SendButtonToolTip));
        OnPropertyChanged(nameof(SendButtonGlyph));
    }
}

public sealed class PendingDocumentAttachmentViewModel(string filePath)
{
    public string FilePath { get; } = filePath;
    public string FileName { get; } = Path.GetFileName(filePath);
    public long FileSizeBytes { get; } = new FileInfo(filePath).Exists ? new FileInfo(filePath).Length : 0;
    public string SizeLabel => $"{FileSizeBytes / 1024.0:0.00} KB";
}
