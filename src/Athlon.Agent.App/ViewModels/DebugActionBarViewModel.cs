using System.Windows;
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
    private readonly IDebugPhaseAccessor _debugPhaseAccessor;
    private readonly IDebugRunStore _debugRunStore;
    private readonly ILocalizationService _loc;

    private Func<string>? _getDisplayedSessionId;
    private Func<AgentSession>? _getSession;
    private Func<SessionTurnUiController>? _getActiveUi;
    private Action<string?, ShellToastKind>? _showToast;

    public DebugActionBarViewModel(
        SessionTurnCoordinator sessionTurns,
        IDebugSessionState debugSessionState,
        IDebugPhaseAccessor debugPhaseAccessor,
        IDebugRunStore debugRunStore,
        ILocalizationService localization)
    {
        _sessionTurns = sessionTurns;
        _debugSessionState = debugSessionState;
        _debugPhaseAccessor = debugPhaseAccessor;
        _debugRunStore = debugRunStore;
        _loc = localization;
        _debugSessionState.RunChanged += (_, _) => RequestRefreshFromActiveRun();
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
        RequestRefreshFromActiveRun();
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
    private bool _showStartFix;

    [ObservableProperty]
    private bool _showReanalyze;

    [ObservableProperty]
    private bool _showNotFixed;

    [ObservableProperty]
    private bool _showFixed;

    private void RequestRefreshFromActiveRun()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(RefreshFromActiveRun);
            return;
        }

        RefreshFromActiveRun();
    }

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
            ShowStartFix = false;
            ShowReanalyze = false;
            ShowNotFixed = false;
            ShowFixed = false;
            NotifyActionCommands();
            return;
        }

        PhaseLabel = string.Format(Strings.Get("Debug_PhaseLabel"), run.Phase);
        Summary = run.Phase is DebugPhase.AwaitFixConfirm or DebugPhase.Analyze
            ? (run.RootCauseSummary ?? run.ReproStepsMarkdown ?? run.BugDescription ?? string.Empty)
            : (run.ReproStepsMarkdown ?? run.BugDescription ?? string.Empty);
        ShowReproduced = run.Phase == DebugPhase.AwaitRepro;
        ShowStartFix = run.Phase == DebugPhase.AwaitFixConfirm;
        ShowReanalyze = run.Phase == DebugPhase.AwaitFixConfirm;
        ShowNotFixed = run.Phase == DebugPhase.AwaitVerify;
        ShowFixed = run.Phase == DebugPhase.AwaitVerify;
        NotifyActionCommands();
    }

    public async Task AbandonActiveRunAsync()
    {
        var sessionId = _getDisplayedSessionId?.Invoke();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        if (_sessionTurns.IsRunning(sessionId))
        {
            _sessionTurns.Cancel(sessionId);
        }

        _debugPhaseAccessor.Clear(sessionId);
        await _debugRunStore.ClearActiveAsync(sessionId).ConfigureAwait(true);
        _debugSessionState.NotifyChanged(null);
        RequestRefreshFromActiveRun();
    }

    [RelayCommand(CanExecute = nameof(CanMarkReproduced))]
    private void MarkReproduced() => StartContinuation(DebugContinuationKind.Reproduced);

    [RelayCommand(CanExecute = nameof(CanStartFix))]
    private void StartFix() => StartContinuation(DebugContinuationKind.StartFix);

    [RelayCommand(CanExecute = nameof(CanReanalyze))]
    private void Reanalyze() => StartContinuation(DebugContinuationKind.Reanalyze);

    [RelayCommand(CanExecute = nameof(CanMarkFixed))]
    private void MarkFixed() => StartContinuation(DebugContinuationKind.VerifiedFixed);

    [RelayCommand(CanExecute = nameof(CanMarkNotFixed))]
    private void MarkNotFixed() => StartContinuation(DebugContinuationKind.VerifiedNotFixed);

    private bool CanMarkReproduced() => ShowReproduced;

    private bool CanStartFix() => ShowStartFix;

    private bool CanReanalyze() => ShowReanalyze;

    private bool CanMarkFixed() => ShowFixed;

    private bool CanMarkNotFixed() => ShowNotFixed;

    private void NotifyActionCommands()
    {
        MarkReproducedCommand.NotifyCanExecuteChanged();
        StartFixCommand.NotifyCanExecuteChanged();
        ReanalyzeCommand.NotifyCanExecuteChanged();
        MarkFixedCommand.NotifyCanExecuteChanged();
        MarkNotFixedCommand.NotifyCanExecuteChanged();
    }

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
