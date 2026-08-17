using Athlon.Agent.Core;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class DebugActionBarViewModel : ObservableObject
{
    private readonly SessionTurnCoordinator _sessionTurns;
    private readonly IDebugSessionState _debugSessionState;
    private readonly ILocalizationService _loc;

    private Func<string>? _getDisplayedSessionId;
    private Func<AgentSession>? _getSession;
    private Func<SessionTurnUiController>? _getActiveUi;
    private Action<string?, ShellToastKind>? _showToast;

    public DebugActionBarViewModel(
        SessionTurnCoordinator sessionTurns,
        IDebugSessionState debugSessionState,
        ILocalizationService localization)
    {
        _sessionTurns = sessionTurns;
        _debugSessionState = debugSessionState;
        _loc = localization;
        _debugSessionState.RunChanged += (_, _) => RefreshFromActiveRun();
    }

    public void Configure(
        Func<string> getDisplayedSessionId,
        Func<AgentSession> getSession,
        Func<SessionTurnUiController> getActiveUi,
        Action<string?, ShellToastKind> showToast)
    {
        _getDisplayedSessionId = getDisplayedSessionId;
        _getSession = getSession;
        _getActiveUi = getActiveUi;
        _showToast = showToast;
        RefreshFromActiveRun();
    }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _phaseLabel = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _showReproduced;

    [ObservableProperty]
    private bool _showVerify;

    public void RefreshFromActiveRun()
    {
        var sessionId = _getDisplayedSessionId?.Invoke();
        var run = string.IsNullOrWhiteSpace(sessionId) ? null : _debugSessionState.GetActiveRun(sessionId);
        IsVisible = run is not null && run.Phase != DebugPhase.Done;
        if (run is null)
        {
            PhaseLabel = string.Empty;
            Summary = string.Empty;
            ShowReproduced = false;
            ShowVerify = false;
            return;
        }

        PhaseLabel = string.Format(Strings.Get("Debug_PhaseLabel"), run.Phase);
        Summary = run.ReproStepsMarkdown ?? run.BugDescription ?? string.Empty;
        ShowReproduced = run.Phase == DebugPhase.AwaitRepro;
        ShowVerify = run.Phase == DebugPhase.AwaitVerify;
    }

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void MarkReproduced() => StartContinuation(DebugContinuationKind.Reproduced);

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void MarkFixed() => StartContinuation(DebugContinuationKind.VerifiedFixed);

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void MarkNotFixed() => StartContinuation(DebugContinuationKind.VerifiedNotFixed);

    private bool CanContinue() => ShowReproduced || ShowVerify;

    private void StartContinuation(DebugContinuationKind kind)
    {
        if (_getDisplayedSessionId is null || _getSession is null || _getActiveUi is null)
        {
            return;
        }

        var sessionId = _getDisplayedSessionId();
        var error = _sessionTurns.TryStartDebugContinuation(
            sessionId,
            _getSession(),
            kind,
            _getActiveUi());
        if (error is not null)
        {
            _showToast?.Invoke(error, ShellToastKind.Error);
        }
    }
}
