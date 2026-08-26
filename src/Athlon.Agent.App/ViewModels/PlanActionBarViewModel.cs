using System.IO;
using System.Windows;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class PlanActionBarViewModel : ObservableObject
{
    private readonly SessionTurnCoordinator _sessionTurns;
    private readonly IPlanTurnOrchestrator _planOrchestrator;
    private readonly IPlanSessionState _planSessionState;
    private readonly IPlanPhaseAccessor _planPhaseAccessor;
    private readonly IPlanRunStore _planRunStore;
    private readonly ILocalizationService _loc;

    private Func<string>? _getDisplayedSessionId;
    private Func<AgentSession>? _getSession;
    private Action<AgentSession>? _setSession;
    private Func<SessionTurnUiController>? _getActiveUi;
    private Action<string?, ShellToastKind>? _showToast;
    private Func<Task>? _onBuildApprovedAsync;
    private Action<string>? _openPlanPreview;
    private Action? _focusComposer;
    private Action<string?>? _setComposerHint;

    public PlanActionBarViewModel(
        SessionTurnCoordinator sessionTurns,
        IPlanTurnOrchestrator planOrchestrator,
        IPlanSessionState planSessionState,
        IPlanPhaseAccessor planPhaseAccessor,
        IPlanRunStore planRunStore,
        ILocalizationService localization)
    {
        _sessionTurns = sessionTurns;
        _planOrchestrator = planOrchestrator;
        _planSessionState = planSessionState;
        _planPhaseAccessor = planPhaseAccessor;
        _planRunStore = planRunStore;
        _loc = localization;
        _planSessionState.RunChanged += OnRunChanged;
    }

    public void Configure(
        Func<string> getDisplayedSessionId,
        Func<AgentSession> getSession,
        Action<AgentSession> setSession,
        Func<SessionTurnUiController> getActiveUi,
        Action<string?, ShellToastKind> showToast,
        Func<Task> onBuildApprovedAsync,
        Action<string> openPlanPreview,
        Action? focusComposer = null,
        Action<string?>? setComposerHint = null)
    {
        _getDisplayedSessionId = getDisplayedSessionId;
        _getSession = getSession;
        _setSession = setSession;
        _getActiveUi = getActiveUi;
        _showToast = showToast;
        _onBuildApprovedAsync = onBuildApprovedAsync;
        _openPlanPreview = openPlanPreview;
        _focusComposer = focusComposer;
        _setComposerHint = setComposerHint;
        RequestRefreshFromActiveRun();
    }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _phaseLabel = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _showBuild;

    [ObservableProperty]
    private bool _showRevise;

    [ObservableProperty]
    private bool _isRevisePending;

    private void OnRunChanged(object? sender, PlanRunChangedEventArgs e)
    {
        RequestRefreshFromActiveRun();
        TryOpenPlanPreview(e.Run);
    }

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
        var run = string.IsNullOrWhiteSpace(sessionId) ? null : _planPhaseAccessor.GetActiveRun(sessionId);
        IsVisible = run is not null && run.Phase == PlanPhase.AwaitConfirm;
        if (run is null || run.Phase != PlanPhase.AwaitConfirm)
        {
            PhaseLabel = string.Empty;
            Summary = string.Empty;
            ShowBuild = false;
            ShowRevise = false;
            ClearRevisePending();
            NotifyActionCommands();
            return;
        }

        PhaseLabel = string.Format(Strings.Get("Plan_PhaseLabel"), run.Phase);
        Summary = !string.IsNullOrWhiteSpace(run.Overview)
            ? run.Overview!
            : (run.Title ?? run.Goal ?? string.Empty);
        ShowBuild = true;
        ShowRevise = true;
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

        ClearRevisePending();
        _planPhaseAccessor.Clear(sessionId);
        await _planRunStore.ClearActiveAsync(sessionId).ConfigureAwait(true);
        _planSessionState.NotifyChanged(null);
        RequestRefreshFromActiveRun();
    }

    /// <summary>If revise is pending, clears it and returns true so Send can start PlanContinuation.Revise.</summary>
    public bool TryConsumeRevisePending()
    {
        if (!IsRevisePending)
        {
            return false;
        }

        ClearRevisePending();
        return true;
    }

    public void ClearRevisePending()
    {
        if (!IsRevisePending)
        {
            return;
        }

        IsRevisePending = false;
        _setComposerHint?.Invoke(null);
    }

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        if (_getDisplayedSessionId is null || _getSession is null)
        {
            return;
        }

        var sessionId = _getDisplayedSessionId();
        if (_sessionTurns.IsRunning(sessionId))
        {
            _showToast?.Invoke(_loc["Plan_BusyCannotBuild"], ShellToastKind.Error);
            return;
        }

        ClearRevisePending();
        try
        {
            var session = await _planOrchestrator.ContinueAsync(
                _getSession(),
                PlanContinuationKind.Build,
                callbacks: null,
                CancellationToken.None).ConfigureAwait(true);
            _setSession?.Invoke(session);
            RefreshFromActiveRun();
            if (_onBuildApprovedAsync is not null)
            {
                await _onBuildApprovedAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _showToast?.Invoke(ex.Message, ShellToastKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevise))]
    private void Revise()
    {
        if (!ShowRevise)
        {
            return;
        }

        if (IsRevisePending)
        {
            ClearRevisePending();
            _showToast?.Invoke(_loc["Plan_ReviseCancelled"], ShellToastKind.Info);
            return;
        }

        IsRevisePending = true;
        _setComposerHint?.Invoke(_loc["Plan_ReviseComposerHint"]);
        _showToast?.Invoke(_loc["Plan_ReviseComposerHint"], ShellToastKind.Info);
        _focusComposer?.Invoke();
    }

    private bool CanBuild() => ShowBuild;

    private bool CanRevise() => ShowRevise;

    private void NotifyActionCommands()
    {
        BuildCommand.NotifyCanExecuteChanged();
        ReviseCommand.NotifyCanExecuteChanged();
    }

    private void TryOpenPlanPreview(PlanRun? run)
    {
        if (run is null
            || string.IsNullOrWhiteSpace(run.PlanPath)
            || run.Phase is not (PlanPhase.Draft or PlanPhase.AwaitConfirm))
        {
            return;
        }

        if (!File.Exists(run.PlanPath))
        {
            return;
        }

        var path = run.PlanPath;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => _openPlanPreview?.Invoke(path));
            return;
        }

        _openPlanPreview?.Invoke(path);
    }
}
