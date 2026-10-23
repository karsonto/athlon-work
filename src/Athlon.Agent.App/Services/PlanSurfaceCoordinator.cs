using Athlon.Agent.App.Controls;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.App.Services;

/// <summary>Plan action bar, approved-plan build, and plan card refresh for the main shell.</summary>
public sealed class PlanSurfaceCoordinator
{
    private readonly IPlanRunStore _planRunStore;
    private readonly IPlanArtifactStore _planArtifactStore;
    private readonly IPlanContinuationTracker _planContinuationTracker;
    private readonly ISessionTaskListStore _taskListStore;
    private readonly ITaskListChangedNotifier _taskListChangedNotifier;
    private readonly SessionTurnCoordinator _sessionTurns;
    private readonly IChatScrollService _chatScroll;
    private readonly ILocalizationService _loc;

    private Func<string>? _getDisplayedSessionId;
    private Func<AgentSession>? _getSession;
    private Action<AgentSession>? _setSession;
    private Func<SessionTurnUiController>? _getActiveUi;
    private Func<PlanActionBarViewModel>? _getPlanBar;
    private Func<ComposerHarnessViewModel>? _getComposerHarness;
    private Func<ChatPageViewModel>? _getChatPage;
    private Action<string, ShellToastKind>? _showShellToast;
    private Action<string?>? _setComposerHint;
    private Func<Task>? _ensureDisplayedSessionReadyAsync;
    private Action? _requestComposerFocus;

    public PlanSurfaceCoordinator(
        IPlanRunStore planRunStore,
        IPlanArtifactStore planArtifactStore,
        IPlanContinuationTracker planContinuationTracker,
        ISessionTaskListStore taskListStore,
        ITaskListChangedNotifier taskListChangedNotifier,
        SessionTurnCoordinator sessionTurns,
        IChatScrollService chatScroll,
        ILocalizationService localization)
    {
        _planRunStore = planRunStore;
        _planArtifactStore = planArtifactStore;
        _planContinuationTracker = planContinuationTracker;
        _taskListStore = taskListStore;
        _taskListChangedNotifier = taskListChangedNotifier;
        _sessionTurns = sessionTurns;
        _chatScroll = chatScroll;
        _loc = localization;
    }

    public void Configure(
        Func<string> getDisplayedSessionId,
        Func<AgentSession> getSession,
        Action<AgentSession> setSession,
        Func<SessionTurnUiController> getActiveUi,
        Func<PlanActionBarViewModel> getPlanBar,
        Func<ComposerHarnessViewModel> getComposerHarness,
        Func<ChatPageViewModel> getChatPage,
        Action<string, ShellToastKind> showShellToast,
        Action<string?> setComposerHint,
        Func<Task> ensureDisplayedSessionReadyAsync,
        Action requestComposerFocus)
    {
        _getDisplayedSessionId = getDisplayedSessionId;
        _getSession = getSession;
        _setSession = setSession;
        _getActiveUi = getActiveUi;
        _getPlanBar = getPlanBar;
        _getComposerHarness = getComposerHarness;
        _getChatPage = getChatPage;
        _showShellToast = showShellToast;
        _setComposerHint = setComposerHint;
        _ensureDisplayedSessionReadyAsync = ensureDisplayedSessionReadyAsync;
        _requestComposerFocus = requestComposerFocus;
    }

    public void WirePlanBar(PlanActionBarViewModel planBar)
    {
        planBar.Configure(
            () => _getDisplayedSessionId!(),
            () => _getSession!(),
            session => _setSession!(session),
            (message, kind) => _showShellToast!(message, kind),
            StartFromApprovedPlanAsync,
            setComposerHint: hint => _setComposerHint!(hint),
            onPlanTimeline: _ => RefreshPlanCard(),
            onPlanTimelineCleared: () => _getActiveUi!().ClearPlanReady(),
            setComposerFocus: () => _requestComposerFocus!());
    }

    public void WireChatPage(ChatPageViewModel chatPage)
    {
        chatPage.PlanBuildRequested += OnPlanBuildRequested;
    }

    public void AttachChatView(WebChatView chatView)
    {
        chatView.PlanBuildRequested += OnPlanBuildRequested;
        chatView.PlanReviseRequested += OnPlanReviseRequested;
    }

    public void DetachChatView(WebChatView chatView)
    {
        chatView.PlanBuildRequested -= OnPlanBuildRequested;
        chatView.PlanReviseRequested -= OnPlanReviseRequested;
    }

    public void UnwireChatPage(ChatPageViewModel chatPage)
    {
        chatPage.PlanBuildRequested -= OnPlanBuildRequested;
    }

    public void OnEnteredPlanMode()
    {
        _getPlanBar!().RefreshFromActiveRun();
        RefreshPlanCard();
    }

    public void OnDisplayedSessionRefreshed()
    {
        _getPlanBar!().RefreshFromActiveRun();
        RefreshPlanCard();
    }

    public bool CancelPlanRevise() => _getPlanBar!().CancelReviseMode();

    /// <summary>
    /// Re-emits the plan card for the displayed session. A plan card is not backed by the
    /// transcript, so the timeline cannot restore it on its own; this is the single refresh path
    /// that keeps it pinned to the run's <see cref="TimelineOrderPolicy.Plan"/> slot.
    /// </summary>
    public void RefreshPlanCard()
    {
        var run = _getPlanBar!().GetActiveRun();
        var activeUi = _getActiveUi!();
        if (run is { Phase: PlanPhase.AwaitConfirm or PlanPhase.Done })
        {
            activeUi.ShowPlanReady(run);
        }
        else
        {
            activeUi.ClearPlanReady();
        }
    }

    private void OnPlanBuildRequested(object? sender, EventArgs e)
    {
        var planBar = _getPlanBar!();
        if (planBar.BuildCommand.CanExecute(null))
        {
            planBar.BuildCommand.Execute(null);
        }
    }

    private void OnPlanReviseRequested(object? sender, EventArgs e)
    {
        if (!_getPlanBar!().EnterReviseMode())
        {
            _showShellToast!(_loc["Plan_BuildMissingPlan"], ShellToastKind.Error);
        }
    }

    public async Task StartFromApprovedPlanAsync()
    {
        await _ensureDisplayedSessionReadyAsync!().ConfigureAwait(true);
        var sessionId = _getDisplayedSessionId!();
        var approved = await _planRunStore.LoadActiveAsync(sessionId).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(approved?.PlanMarkdown))
        {
            _showShellToast!(_loc["Plan_BuildMissingPlan"], ShellToastKind.Error);
            return;
        }

        await _planArtifactStore
            .SaveAsync(sessionId, approved.PlanMarkdown, approved)
            .ConfigureAwait(true);

        var existingById = (await _taskListStore.GetAsync(sessionId).ConfigureAwait(true))
            .Items
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var seeds = approved.Todos
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Content))
            .Select(t => new AgentTaskItem
            {
                Id = t.Id,
                Content = t.Content,
                Status = existingById.TryGetValue(t.Id, out var existing)
                    ? existing.Status
                    : AgentTaskStatuses.Pending
            })
            .ToList();
        if (seeds.Count > 0)
        {
            await _taskListStore.ApplyMergeAsync(sessionId, seeds, merge: true).ConfigureAwait(true);
            _taskListChangedNotifier.Notify(sessionId);
        }

        _planContinuationTracker.Reset(sessionId);

        var composerHarness = _getComposerHarness!();
        if (composerHarness.SelectModeCommand.CanExecute(SessionAgentMode.Agent))
        {
            await composerHarness.SelectModeCommand.ExecuteAsync(SessionAgentMode.Agent).ConfigureAwait(true);
        }

        var ui = _sessionTurns.GetOrCreateUi(
            sessionId,
            _chatScroll.ScrollToBottom,
            _chatScroll.ScrollToBottomImmediate);
        ui.ResetForTurn();
        var session = _getSession!();
        var error = _sessionTurns.TryStartTurn(
            sessionId,
            session,
            BuildApprovedPlanMessage(approved.PlanMarkdown, seeds.Count > 0),
            Array.Empty<ImageAttachment>(),
            ui,
            appendUserMessage: true);
        if (error is not null)
        {
            _showShellToast!(error, ShellToastKind.Error);
            return;
        }

        await _getPlanBar!().ClearActiveRunAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Builds the hidden approved-plan message, appending the merge guidance the model needs to
    /// update a pre-seeded list instead of overwriting it.
    /// </summary>
    public static string BuildApprovedPlanMessage(string markdown, bool taskListSeeded)
    {
        var message = ApprovedPlanPrompt.BuildUserMessage(markdown);
        return taskListSeeded
            ? message
              + "\n\nThe session task list is already seeded from this plan. "
              + "Update with todo_write merge=true only — merge=false would discard the seeded list."
            : message;
    }
}
