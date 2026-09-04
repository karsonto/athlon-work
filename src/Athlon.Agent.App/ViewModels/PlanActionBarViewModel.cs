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
    private Action<string?, ShellToastKind>? _showToast;
    private Func<Task>? _onBuildApprovedAsync;
    private Action<PlanRun>? _onPlanTimeline;
    private Action<string?>? _setComposerHint;
    private string? _lastTimelineKey;

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
        Action<string?, ShellToastKind> showToast,
        Func<Task> onBuildApprovedAsync,
        Action<PlanRun>? onPlanTimeline = null,
        Action<string?>? setComposerHint = null)
    {
        _getDisplayedSessionId = getDisplayedSessionId;
        _getSession = getSession;
        _setSession = setSession;
        _showToast = showToast;
        _onBuildApprovedAsync = onBuildApprovedAsync;
        _onPlanTimeline = onPlanTimeline;
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
    private string _todosSummary = string.Empty;

    [ObservableProperty]
    private bool _showBuild;

    private void OnRunChanged(object? sender, PlanRunChangedEventArgs e)
    {
        RequestRefreshFromActiveRun();
        DispatchPlanTimeline(e.Run);
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
        if (run is null && !string.IsNullOrWhiteSpace(sessionId))
        {
            _ = HydrateActiveRunAsync(sessionId);
        }
        ApplyComposerHint(run);
        IsVisible = run is not null && run.Phase == PlanPhase.AwaitConfirm;
        if (run is null || run.Phase != PlanPhase.AwaitConfirm)
        {
            PhaseLabel = string.Empty;
            Summary = string.Empty;
            TodosSummary = string.Empty;
            ShowBuild = false;
            NotifyActionCommands();
            return;
        }

        ShowBuild = true;
        NotifyActionCommands();
    }

    public PlanRun? GetActiveRun()
    {
        var sessionId = _getDisplayedSessionId?.Invoke();
        return string.IsNullOrWhiteSpace(sessionId) ? null : _planPhaseAccessor.GetActiveRun(sessionId);
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

        _planPhaseAccessor.Clear(sessionId);
        await _planRunStore.ClearActiveAsync(sessionId).ConfigureAwait(true);
        _planSessionState.NotifyChanged(null);
        _lastTimelineKey = null;
        _setComposerHint?.Invoke(null);
        RequestRefreshFromActiveRun();
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

    private bool CanBuild() => ShowBuild;

    private void NotifyActionCommands() => BuildCommand.NotifyCanExecuteChanged();

    private async Task HydrateActiveRunAsync(string sessionId)
    {
        try
        {
            var stored = await _planRunStore.LoadActiveAsync(sessionId).ConfigureAwait(true);
            if (stored is null || !string.Equals(sessionId, _getDisplayedSessionId?.Invoke(), StringComparison.Ordinal))
            {
                return;
            }

            _planPhaseAccessor.SetActiveRun(stored);
            _planSessionState.NotifyChanged(stored);
        }
        catch
        {
            // Hydration is best-effort; the next user turn reloads from the store.
        }
    }

    private void ApplyComposerHint(PlanRun? run)
    {
        if (run is null)
        {
            _setComposerHint?.Invoke(null);
            return;
        }

        if (run.Phase == PlanPhase.AwaitConfirm)
        {
            _setComposerHint?.Invoke(_loc["Plan_ConfirmComposerHint"]);
            return;
        }

        if (run.Phase == PlanPhase.AwaitClarify)
        {
            _setComposerHint?.Invoke(_loc["Plan_ClarifyComposerHint"]);
            return;
        }

        _setComposerHint?.Invoke(null);
    }

    private void DispatchPlanTimeline(PlanRun? run)
    {
        // Only the AwaitConfirm "plan ready" state is shown in the timeline now;
        // ask_user / AwaitClarify renders exclusively in the composer QuestionBar.
        if (run is null || run.Phase != PlanPhase.AwaitConfirm)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        var key = "ready:" + run.Id + ":" + run.UpdatedAt.ToUnixTimeMilliseconds();
        if (string.Equals(key, _lastTimelineKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastTimelineKey = key;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => _onPlanTimeline?.Invoke(run));
            return;
        }

        _onPlanTimeline?.Invoke(run);
    }
}
