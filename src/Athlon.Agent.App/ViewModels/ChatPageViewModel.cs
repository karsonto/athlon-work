using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.Services.Speech;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ChatPageViewModel : ObservableObject, IDisposable
{
    private readonly ComposerCoordinator _composer;
    private readonly SessionTurnCoordinator _sessionTurns;
    private readonly IImageAttachmentReader _imageAttachmentReader;
    private readonly IChatDocumentAttachmentExtractor _documentExtractor;
    private readonly IChatScrollService _chatScroll;
    private readonly ISpeechToTextService _speechToText;
    private readonly ILocalizationService _loc;
    private readonly IPlanPhaseAccessor _planPhaseAccessor;
    private readonly IUserQuestionState _userQuestions;
    private string? _speechDraftBase;
    private bool _disposed;

    private Func<string>? _getDisplayedSessionId;
    private Func<AgentSession>? _getSession;
    private Func<SessionTurnUiController>? _getActiveUi;
    private Action<string, ShellToastKind>? _showShellToast;
    private Action<string?>? _setComposerStatus;
    private Action? _notifyCommandStatesChanged;
    private Action? _syncWorkspaceContext;
    private Action<bool>? _setIsBusy;
    private Func<IReadOnlyList<string>>? _getIgnorePatterns;
    private Func<bool>? _tryCancelCompaction;
    private Func<ComposerSlashCommandContext>? _createSlashCommandContext;
    private Func<Task>? _ensureSessionReady;

    public event EventHandler? FocusComposerRequested;

    public event EventHandler? PlanBuildRequested;

    public ChatPageViewModel(
        ComposerCoordinator composer,
        SessionTurnCoordinator sessionTurns,
        IImageAttachmentReader imageAttachmentReader,
        IChatDocumentAttachmentExtractor documentExtractor,
        IChatScrollService chatScroll,
        ISpeechToTextService speechToText,
        ILocalizationService localization,
        IPlanPhaseAccessor planPhaseAccessor,
        IUserQuestionState userQuestions)
    {
        _composer = composer;
        _sessionTurns = sessionTurns;
        _imageAttachmentReader = imageAttachmentReader;
        _documentExtractor = documentExtractor;
        _chatScroll = chatScroll;
        _speechToText = speechToText;
        _loc = localization;
        _planPhaseAccessor = planPhaseAccessor;
        _userQuestions = userQuestions;

        _speechToText.AvailabilityChanged += OnSpeechAvailabilityChanged;
        _speechToText.PartialText += OnSpeechPartialText;
        _speechToText.FinalText += OnSpeechFinalText;
        _speechToText.Failed += OnSpeechFailed;
        AppCultureManager.CultureChanged += OnCultureChanged;

        _ = InitializeSpeechAsync();
    }

    public void Configure(
        Func<string> getDisplayedSessionId,
        Func<AgentSession> getSession,
        Func<SessionTurnUiController> getActiveUi,
        Action<string, ShellToastKind> showShellToast,
        Action<string?> setComposerStatus,
        Action notifyCommandStatesChanged,
        Action syncWorkspaceContext,
        Action<bool> setIsBusy,
        Func<IReadOnlyList<string>> getIgnorePatterns,
        Func<bool> tryCancelCompaction,
        Func<ComposerSlashCommandContext> createSlashCommandContext,
        Func<Task> ensureSessionReady)
    {
        _getDisplayedSessionId = getDisplayedSessionId;
        _getSession = getSession;
        _getActiveUi = getActiveUi;
        _showShellToast = showShellToast;
        _setComposerStatus = setComposerStatus;
        _notifyCommandStatesChanged = notifyCommandStatesChanged;
        _syncWorkspaceContext = syncWorkspaceContext;
        _setIsBusy = setIsBusy;
        _getIgnorePatterns = getIgnorePatterns;
        _tryCancelCompaction = tryCancelCompaction;
        _createSlashCommandContext = createSlashCommandContext;
        _ensureSessionReady = ensureSessionReady;
    }

    public void RequestFocusComposer() => FocusComposerRequested?.Invoke(this, EventArgs.Empty);

    [ObservableProperty]
    private string composerText = string.Empty;

    /// <summary>
    /// Per-session composer drafts, so switching A -&gt; B -&gt; A restores what the user had typed in A.
    /// A session that was never visited has no entry and therefore starts empty, matching the old
    /// unconditional-clear behaviour for a fresh conversation.
    /// </summary>
    private readonly Dictionary<string, string> _composerDrafts = new(StringComparer.Ordinal);

    /// <summary>Remembers the current draft text for a session before the shell switches away.</summary>
    public void SaveComposerDraft(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _composerDrafts[sessionId] = ComposerText ?? string.Empty;
    }

    /// <summary>Restores a session's draft text (empty when it was never visited).</summary>
    public void RestoreComposerDraft(string? sessionId)
    {
        ComposerText = sessionId is not null && _composerDrafts.TryGetValue(sessionId, out var draft)
            ? draft
            : string.Empty;
    }

    /// <summary>Drops a session's draft (deleted conversation).</summary>
    public void ForgetComposerDraft(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            _composerDrafts.Remove(sessionId);
        }
    }


    public bool IsComposerEmpty => string.IsNullOrWhiteSpace(ComposerText);


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

        // A session switch loads the display page first and the full session payload shortly
        // after. Sending before it lands would start the turn from an empty message list, so wait
        // for the payload before consuming the composer.
        if (_ensureSessionReady is not null)
        {
            await _ensureSessionReady().ConfigureAwait(true);
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
                    _showShellToast?.Invoke(
                        failures.Count > 0 ? failures[0] : Strings.Get("Chat_AttachmentParseFailed"),
                        ShellToastKind.Error);
                    return;
                }

                if (failures.Count > 0)
                {
                    _showShellToast?.Invoke(
                        Strings.Format("Chat_AttachmentParsePartial", extractionResults.Count, failures.Count),
                        ShellToastKind.Error);
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

        var planPhase = _planPhaseAccessor.GetPhase(displayedSessionId);
        if (planPhase == PlanPhase.AwaitConfirm
            && PendingImageAttachments.Count == 0
            && extractionResults.Count == 0
            && !string.IsNullOrWhiteSpace(input))
        {
            if (_sessionTurns.IsRunning(displayedSessionId))
            {
                _showShellToast!.Invoke(_loc["Plan_BusyCannotRevise"], ShellToastKind.Error);
                return;
            }

            var intent = PlanBuildIntent.Classify(input);
            if (intent == PlanSubmitIntent.Build)
            {
                ComposerText = string.Empty;
                _userQuestions.Clear(displayedSessionId);
                PlanBuildRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (intent == PlanSubmitIntent.Ask)
            {
                ComposerText = string.Empty;
                var english = _loc.CurrentCulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase);
                _userQuestions.SetPending(displayedSessionId, PlanBuildIntent.CreateQuestion(english));
                return;
            }
        }

        var imageAttachments = _composer.PersistPendingImages(displayedSessionId, PendingImageAttachments);
        var revisePlan = planPhase == PlanPhase.AwaitConfirm;
        if (revisePlan && string.IsNullOrWhiteSpace(input))
        {
            _showShellToast!.Invoke(_loc["Plan_ReviseRequiresInput"], ShellToastKind.Error);
            return;
        }

        if (revisePlan && _sessionTurns.IsRunning(displayedSessionId))
        {
            _showShellToast!.Invoke(_loc["Plan_BusyCannotRevise"], ShellToastKind.Error);
            return;
        }

        ComposerText = string.Empty;
        _syncWorkspaceContext!();

        var ui = _sessionTurns.GetOrCreateUi(displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        PendingImageAttachments.Clear();
        PendingDocumentAttachments.Clear();
        OnPendingDocumentsChanged();

        if (_sessionTurns.IsRunning(displayedSessionId))
        {
            _sessionTurns.EnqueueTurn(displayedSessionId, input, imageAttachments, ui);
            _showShellToast!.Invoke(Strings.Get("Chat_QueuedStatus"), ShellToastKind.Info);
            _notifyCommandStatesChanged!();
            return;
        }

        ui.AddUserMessage(input, imageAttachments);
        var error = revisePlan
            ? _sessionTurns.TryStartPlanContinuation(
                displayedSessionId,
                session,
                PlanContinuationKind.Revise,
                ui,
                input)
            : _sessionTurns.TryStartTurn(displayedSessionId, session, input, imageAttachments, ui);

        if (error is not null)
        {
            _showShellToast!.Invoke(error, ShellToastKind.Error);
            _notifyCommandStatesChanged!();
            return;
        }

        UpdateDisplayedBusyState();
        _notifyCommandStatesChanged!();
    }

    public async Task<bool> TrySubmitPlanInputAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input)
            || _getDisplayedSessionId is null
            || _getSession is null
            || _getActiveUi is null)
        {
            return false;
        }

        var displayedSessionId = _getDisplayedSessionId();
        if (_sessionTurns.IsRunning(displayedSessionId))
        {
            _showShellToast?.Invoke(_loc["AskUser_BusyCannotSubmit"], ShellToastKind.Error);
            return false;
        }

        if (_ensureSessionReady is not null)
        {
            await _ensureSessionReady().ConfigureAwait(true);
        }

        var trimmed = input.Trim();
        if (_planPhaseAccessor.GetPhase(displayedSessionId) == PlanPhase.AwaitConfirm
            && PlanBuildIntent.TryParseChoice(trimmed, out var choice))
        {
            _userQuestions.Clear(displayedSessionId);
            if (choice == PlanBuildChoice.Build)
            {
                PlanBuildRequested?.Invoke(this, EventArgs.Empty);
                return true;
            }

            _showShellToast?.Invoke(_loc["Plan_ReviseComposerHint"], ShellToastKind.Info);
            FocusComposerRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        var session = _getSession();
        _syncWorkspaceContext?.Invoke();
        var ui = _sessionTurns.GetOrCreateUi(displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        ui.AddUserMessage(trimmed, Array.Empty<ImageAttachment>());
        var error = _sessionTurns.TryStartTurn(
            displayedSessionId,
            session,
            trimmed,
            Array.Empty<ImageAttachment>(),
            ui);
        if (error is not null)
        {
            _showShellToast?.Invoke(error, ShellToastKind.Error);
            _notifyCommandStatesChanged?.Invoke();
            return false;
        }

        UpdateDisplayedBusyState();
        _notifyCommandStatesChanged?.Invoke();
        return true;
    }

    public async Task<bool> SendComputerUseAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || IsReadingAttachments)
        {
            return false;
        }

        var displayedSessionId = _getDisplayedSessionId!();
        if (_sessionTurns.IsRunning(displayedSessionId))
        {
            _showShellToast!.Invoke(Strings.Get("Chat_QueuedStatus"), ShellToastKind.Info);
            return false;
        }

        // Same readiness gate as SendAsync: a metadata-only session has no context to send.
        if (_ensureSessionReady is not null)
        {
            await _ensureSessionReady().ConfigureAwait(true);
        }

        var input = prompt.Trim();
        var session = _getSession!();
        var imageAttachments = Array.Empty<ImageAttachment>();
        _syncWorkspaceContext!();

        var ui = _sessionTurns.GetOrCreateUi(
            displayedSessionId,
            RequestScrollToBottom,
            RequestScrollToBottomImmediate);
        ui.AddUserMessage(input, imageAttachments);
        var error = _sessionTurns.TryStartTurn(
            displayedSessionId,
            session,
            input,
            imageAttachments,
            ui,
            computerUseActive: true);
        if (error is not null)
        {
            _showShellToast!.Invoke(error, ShellToastKind.Error);
            _notifyCommandStatesChanged!();
            return false;
        }

        ComposerText = string.Empty;
        UpdateDisplayedBusyState();
        _notifyCommandStatesChanged!();
        return true;
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


    private void RequestScrollToBottom() => _chatScroll.ScrollToBottom();

    private void RequestScrollToBottomImmediate() => _chatScroll.ScrollToBottomImmediate();

    // Keep the send button enabled so long-press speech works even with an empty composer.
    // SendAsync still no-ops when there is nothing to send.
    private bool CanSend() => !IsReadingAttachments;

    partial void OnComposerTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsComposerEmpty));
        SendCommand.NotifyCanExecuteChanged();
    }


    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _speechToText.AvailabilityChanged -= OnSpeechAvailabilityChanged;
        _speechToText.PartialText -= OnSpeechPartialText;
        _speechToText.FinalText -= OnSpeechFinalText;
        _speechToText.Failed -= OnSpeechFailed;
        AppCultureManager.CultureChanged -= OnCultureChanged;
    }
}
