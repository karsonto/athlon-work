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
    private Action? _onPlanTimelineCleared;
    private Action? _setComposerFocus;
    private string? _lastTimelineKey;
    private string? _lastRevisionNoticeKey;

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
        Action<string?>? setComposerHint = null,
        Action? onPlanTimelineCleared = null,
        Action? setComposerFocus = null)
    {
        _getDisplayedSessionId = getDisplayedSessionId;
        _getSession = getSession;
        _setSession = setSession;
        _showToast = showToast;
        _onBuildApprovedAsync = onBuildApprovedAsync;
        _onPlanTimeline = onPlanTimeline;
        _setComposerHint = setComposerHint;
        _onPlanTimelineCleared = onPlanTimelineCleared;
        _setComposerFocus = setComposerFocus;
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
        SyncReviseState(e.Run);
    }

    /// <summary>
    /// Keeps the revision affordance state honest across turn outcomes, and tells the user when a
    /// revision turn ended without producing a new plan. Previously the old plan was kept silently,
    /// so the user believed their edit had been applied.
    /// </summary>
    private void SyncReviseState(PlanRun? run)
    {
        if (run is null)
        {
            IsRevising = false;
            return;
        }

        if (run.RevisionProducedNewPlan == true)
        {
            // A new plan is on the card; the revise round is over.
            IsRevising = false;
            return;
        }

        if (run.Phase == PlanPhase.Done)
        {
            // Build consumed the plan, so any pending revise intent is stale.
            IsRevising = false;
            return;
        }

        if (run.Phase != PlanPhase.AwaitConfirm)
        {
            return;
        }

        if (run.RevisionProducedNewPlan != false)
        {
            return;
        }

        IsRevising = false;
        var key = run.Id + ":" + run.UpdatedAt.ToUnixTimeMilliseconds();
        if (string.Equals(key, _lastRevisionNoticeKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastRevisionNoticeKey = key;
        _showToast?.Invoke(_loc["Plan_ReviseNoNewPlan"], ShellToastKind.Info);
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
        // Composer no longer hosts Build — only the timeline plan card does.
        IsVisible = false;
        ShowBuild = run is not null && run.Phase == PlanPhase.AwaitConfirm;
        if (!ShowBuild)
        {
            PhaseLabel = string.Empty;
            Summary = string.Empty;
            TodosSummary = string.Empty;
        }

        NotifyActionCommands();
    }

    public PlanRun? GetActiveRun()
    {
        var sessionId = _getDisplayedSessionId?.Invoke();
        return string.IsNullOrWhiteSpace(sessionId) ? null : _planPhaseAccessor.GetActiveRun(sessionId);
    }

    /// <summary>
    /// Drops the session's in-memory plan run and its timeline card. Called after Build consumes
    /// the plan, and when the user abandons a draft. Does not touch a running turn.
    /// </summary>
    public async Task ClearActiveRunAsync()
    {
        var sessionId = _getDisplayedSessionId?.Invoke();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _planPhaseAccessor.Clear(sessionId);
        await _planRunStore.ClearActiveAsync(sessionId).ConfigureAwait(true);
        _planSessionState.NotifyChanged(null);
        _lastTimelineKey = null;
        _setComposerHint?.Invoke(null);
        // The plan card is not transcript-backed, so a fresh replay cannot drop it for us.
        _onPlanTimelineCleared?.Invoke();
        RequestRefreshFromActiveRun();
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

        await ClearActiveRunAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Drops the timeline plan card and the composer hint when a run was already cleared
    /// elsewhere (for example by <see cref="ISessionPlanArtifactsClearer"/>). Only the displayed
    /// session's UI state is touched; other sessions keep their own cards.
    /// </summary>
    public void NotifyRunCleared(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || !string.Equals(sessionId, _getDisplayedSessionId?.Invoke(), StringComparison.Ordinal))
        {
            return;
        }

        _lastTimelineKey = null;
        _setComposerHint?.Invoke(null);
        _onPlanTimelineCleared?.Invoke();
        RequestRefreshFromActiveRun();
    }

    /// <summary>
    /// True once the user asked to revise the plan from the card. The composer is focused so the
    /// user can type the change; sending it routes through the existing Revise continuation
    /// (see <c>ChatPageViewModel</c>'s AwaitConfirm handling), which is why no turn starts here.
    /// </summary>
    public bool IsRevising { get; private set; }

    /// <summary>
    /// Enters revision mode for the displayed session's plan: focuses the composer and shows the
    /// revision hint. Revising is a normal typed message, so this only sets up affordances.
    /// </summary>
    public bool EnterReviseMode()
    {
        var run = GetActiveRun();
        if (run is not { Phase: PlanPhase.AwaitConfirm })
        {
            return false;
        }

        IsRevising = true;
        _setComposerHint?.Invoke(_loc["Plan_ReviseComposerHint"]);
        _setComposerFocus?.Invoke();
        return true;
    }

    /// <summary>
    /// Leaves revision mode without touching the run; the plan stays as it was. Returns false when
    /// revise mode was not active, so callers can leave Escape and similar keys untouched.
    /// </summary>
    public bool CancelReviseMode()
    {
        if (!IsRevising)
        {
            return false;
        }

        IsRevising = false;
        ApplyComposerHint(_getDisplayedSessionId is null ? null : GetActiveRun());
        _showToast?.Invoke(_loc["Plan_ReviseCancelled"], ShellToastKind.Info);
        return true;
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
            // An explicit "Revise" click wins over the generic hint so the user gets the
            // instruction that matches the affordance they just used.
            _setComposerHint?.Invoke(
                IsRevising ? _loc["Plan_ReviseComposerHint"] : _loc["Plan_ConfirmComposerHint"]);
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
        // AwaitConfirm: show/update the plan-ready card (Build enabled).
        // Done: refresh the same card with Build disabled after the user builds.
        if (run is null
            || run.Phase is not (PlanPhase.AwaitConfirm or PlanPhase.Done))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        var built = run.Phase == PlanPhase.Done;
        var key = (built ? "built:" : "ready:") + run.Id + ":" + run.UpdatedAt.ToUnixTimeMilliseconds();
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
