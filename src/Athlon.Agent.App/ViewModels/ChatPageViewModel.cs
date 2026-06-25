using System.Collections.ObjectModel;
using System.Windows;
using Athlon.Agent.App.Services;
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
    private readonly IChatScrollService _chatScroll;

    private Func<string>? _getDisplayedSessionId;
    private Func<AgentSession>? _getSession;
    private Func<SessionTurnUiController>? _getActiveUi;
    private Action<string>? _setSettingsStatus;
    private Action? _notifyCommandStatesChanged;
    private Action? _syncWorkspaceContext;
    private Action<bool>? _setIsBusy;
    private Func<IReadOnlyList<string>>? _getIgnorePatterns;

    public ChatPageViewModel(
        ComposerCoordinator composer,
        SessionTurnCoordinator sessionTurns,
        IImageAttachmentReader imageAttachmentReader,
        IChatScrollService chatScroll)
    {
        _composer = composer;
        _sessionTurns = sessionTurns;
        _imageAttachmentReader = imageAttachmentReader;
        _chatScroll = chatScroll;
    }

    public void Configure(
        Func<string> getDisplayedSessionId,
        Func<AgentSession> getSession,
        Func<SessionTurnUiController> getActiveUi,
        Action<string> setSettingsStatus,
        Action notifyCommandStatesChanged,
        Action syncWorkspaceContext,
        Action<bool> setIsBusy,
        Func<IReadOnlyList<string>> getIgnorePatterns)
    {
        _getDisplayedSessionId = getDisplayedSessionId;
        _getSession = getSession;
        _getActiveUi = getActiveUi;
        _setSettingsStatus = setSettingsStatus;
        _notifyCommandStatesChanged = notifyCommandStatesChanged;
        _syncWorkspaceContext = syncWorkspaceContext;
        _setIsBusy = setIsBusy;
        _getIgnorePatterns = getIgnorePatterns;
    }

    [ObservableProperty]
    private string composerText = string.Empty;

    [ObservableProperty]
    private bool isAtCompletionOpen;

    [ObservableProperty]
    private int selectedAtCompletionIndex = -1;

    public ObservableCollection<AtCompletionItemViewModel> AtCompletionItems { get; } = new();
    public ObservableCollection<PendingImageAttachmentViewModel> PendingImageAttachments { get; } = new();
    public bool HasPendingImages => PendingImageAttachments.Count > 0;
    public bool IsComposerEmpty => string.IsNullOrWhiteSpace(ComposerText);

    public void OnPendingImagesChanged() => OnPropertyChanged(nameof(HasPendingImages));

    [RelayCommand]
    private async Task SelectImagesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Multiselect = true,
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.gif"
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var images = await _imageAttachmentReader.ReadImagesAsync(dialog.FileNames).ConfigureAwait(true);
        AddPendingImages(images);
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

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        CloseAtCompletion();

        if (string.IsNullOrWhiteSpace(ComposerText) && PendingImageAttachments.Count == 0)
        {
            return;
        }

        var displayedSessionId = _getDisplayedSessionId!();
        var session = _getSession!();
        _sessionTurns.ReloadSkills();
        var input = _sessionTurns.ExpandComposerInput(ComposerText);
        var imageAttachments = _composer.PersistPendingImages(displayedSessionId, PendingImageAttachments);
        ComposerText = string.Empty;
        _syncWorkspaceContext!();

        var ui = _sessionTurns.GetOrCreateUi(displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        PendingImageAttachments.Clear();

        if (_sessionTurns.IsRunning(displayedSessionId))
        {
            _sessionTurns.EnqueueTurn(displayedSessionId, input, imageAttachments, ui);
            _setSettingsStatus!("已加入排队");
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
            out newCaretIndex);

    public void CloseAtCompletion() =>
        _composer.CloseAtCompletion(
            AtCompletionItems,
            _ => IsAtCompletionOpen = false,
            index => SelectedAtCompletionIndex = index);

    private void RequestScrollToBottom() => _chatScroll.ScrollToBottom();

    private void RequestScrollToBottomImmediate() => _chatScroll.ScrollToBottomImmediate();

    private bool CanSend() =>
        !string.IsNullOrWhiteSpace(ComposerText) || PendingImageAttachments.Count > 0;

    partial void OnComposerTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsComposerEmpty));
        SendCommand.NotifyCanExecuteChanged();
    }
}
