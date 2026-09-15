using System.Collections.Concurrent;
using System.Windows;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Keeps an approved Session Plan running to completion.
///
/// <para>A single model turn routinely ends with tasks still open — the model narrates a summary
/// and stops, waiting for the user. Without this service "follow the plan" depends on the user
/// noticing and prodding. Mirrors <see cref="SubAgentCompletionContinuationService"/>: it observes
/// <see cref="SessionTurnHost.TurnCompleted"/> and starts a new turn while open tasks remain.</para>
///
/// <para>Runaway protection comes from a per-session round budget, a user-initiated stop, a pending
/// <c>ask_user</c> question, and queued user input — any of which ends automatic continuation.</para>
/// </summary>
public sealed class PlanExecutionContinuationService : IDisposable
{
    private static readonly Action NoOpScroll = () => { };

    private readonly SessionTurnCoordinator _sessionTurns;
    private readonly SessionUiCache _uiCache;
    private readonly IFileStorageService _storage;
    private readonly ISessionTaskListStore _taskListStore;
    private readonly IPlanArtifactStore _planArtifactStore;
    private readonly IPlanContinuationTracker _tracker;
    private readonly IUserQuestionState _userQuestions;
    private readonly ISessionHarnessState _harnessState;
    private readonly AppSettings _settings;
    private readonly IAppLogger _logger;

    /// <summary>Sessions whose completion still needs evaluating because a turn was already running.</summary>
    private readonly ConcurrentDictionary<string, byte> _pendingAfterTurn = new(StringComparer.Ordinal);

    private bool _disposed;

    public PlanExecutionContinuationService(
        SessionTurnCoordinator sessionTurns,
        SessionUiCache uiCache,
        IFileStorageService storage,
        ISessionTaskListStore taskListStore,
        IPlanArtifactStore planArtifactStore,
        IPlanContinuationTracker tracker,
        IUserQuestionState userQuestions,
        ISessionHarnessState harnessState,
        AppSettings settings,
        IAppLogger logger)
    {
        _sessionTurns = sessionTurns;
        _uiCache = uiCache;
        _storage = storage;
        _taskListStore = taskListStore;
        _planArtifactStore = planArtifactStore;
        _tracker = tracker;
        _userQuestions = userQuestions;
        _harnessState = harnessState;
        _settings = settings;
        _logger = logger.ForContext("PlanExecutionContinuationService");
        _sessionTurns.TurnHost.TurnCompleted += OnTurnCompleted;
    }

    /// <summary>Current auto-continuation round count for a session (0 when none).</summary>
    public int GetRoundCount(string sessionId) => _tracker.GetCount(sessionId);

    /// <summary>True when the user stopped auto-continuation for this session.</summary>
    public bool IsStopped(string sessionId) => _tracker.IsStopped(sessionId);

    public bool IsEnabled => _settings.AgentTurn.ResolvePlanAutoContinueEnabled();

    /// <summary>
    /// Stops automatic continuation for the session. The current turn is left alone; the user can
    /// still drive the plan manually and a new Build resets the budget.
    /// </summary>
    public void Stop(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _tracker.Stop(sessionId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionTurns.TurnHost.TurnCompleted -= OnTurnCompleted;
    }

    private void OnTurnCompleted(object? sender, SessionTurnCompletedEventArgs e)
    {
        // Auto-continue turns are evaluated too: that is how the loop advances from one round to
        // the next. Nothing is evaluated while a turn is in flight, so a failed round cannot spin.
        _ = ScheduleEvaluateAsync(e.SessionId);
    }

    private async Task ScheduleEvaluateAsync(string sessionId)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        try
        {
            await dispatcher
                .InvokeAsync(async () => await TryStartContinuationAsync(sessionId).ConfigureAwait(true))
                .Task
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning("Plan auto-continuation failed for session {SessionId}: {Error}", sessionId, ex.Message);
        }
    }

    private async Task TryStartContinuationAsync(string sessionId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        if (_sessionTurns.IsRunning(sessionId))
        {
            _pendingAfterTurn.TryAdd(sessionId, 0);
            return;
        }

        _pendingAfterTurn.TryRemove(sessionId, out _);

        if (!IsEnabled || _tracker.IsStopped(sessionId))
        {
            return;
        }

        // A pending question means the model is waiting on the user: auto-continuing would
        // trample the QuestionBar and the model would re-ask.
        if (_userQuestions.GetPending(sessionId) is not null)
        {
            return;
        }

        if (_sessionTurns.TurnHost.HasQueuedTurns(sessionId))
        {
            return;
        }

        // Only continue plans that were actually built and are being executed in Agent mode.
        if (_harnessState.GetMode(sessionId) != SessionAgentMode.Agent)
        {
            return;
        }

        var artifact = await _planArtifactStore.LoadAsync(sessionId).ConfigureAwait(true);
        if (artifact is null || !artifact.HasContent)
        {
            return;
        }

        var list = await _taskListStore.GetAsync(sessionId).ConfigureAwait(true);
        if (!HasOpenWork(list))
        {
            return;
        }

        var budget = _settings.AgentTurn.ResolvePlanAutoContinueMaxRounds();
        if (_tracker.GetCount(sessionId) >= budget)
        {
            _logger.Information(
                "Plan auto-continuation budget exhausted for session {SessionId} after {Rounds} rounds",
                sessionId,
                budget);
            return;
        }

        var session = await _storage.LoadSessionAsync(sessionId).ConfigureAwait(true);
        if (session is null)
        {
            return;
        }

        var ui = _uiCache.GetOrCreate(sessionId, NoOpScroll, NoOpScroll);
        var request = new SessionTurnRequest(
            sessionId,
            session,
            PlanContinuePrompt.BuildUserMessage(),
            Array.Empty<ImageAttachment>(),
            ui,
            IsAutoContinue: true,
            AppendUserMessage: true);

        if (_sessionTurns.TurnHost.TryStart(request, out _))
        {
            var round = _tracker.Increment(sessionId);
            _logger.Information(
                "Plan auto-continuation round {Round} started for session {SessionId}",
                round,
                sessionId);
            return;
        }

        // The host refused (another turn won the race); re-evaluate after it finishes.
        _pendingAfterTurn.TryAdd(sessionId, 0);
    }

    /// <summary>An open item is anything the model still owes work on.</summary>
    internal static bool HasOpenWork(SessionTaskList list) =>
        list.Items.Any(item =>
            string.Equals(item.Status, AgentTaskStatuses.Pending, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Status, AgentTaskStatuses.InProgress, StringComparison.OrdinalIgnoreCase));
}
