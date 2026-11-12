using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Team 运行/Trace 生命周期：按 rootSessionId 管理运行 CTS 与 pending ask，
/// 通过事件通知宿主 VM（IsBusy 刷新、Trace 开窗、Trace 深链状态）。
/// </summary>
public sealed class TeamRunController
{
    private readonly ITeamTaskOrchestrator _orchestrator;
    private readonly ITeamTaskStore _taskStore;
    private readonly ITeamTraceStore _traceStore;
    private readonly AppSettings _settings;
    private readonly IAppLogger _logger;
    private readonly Dictionary<string, CancellationTokenSource> _runCtsBySession =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string RootSessionId, string TaskId)> _pendingAsksBySession =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<bool>? BusyChanged;
    public event EventHandler<(TeamTask Task, bool Live)>? TraceRequested;
    public event EventHandler<string>? TraceStatusChanged;

    public TeamRunController(
        ITeamTaskOrchestrator orchestrator,
        ITeamTaskStore taskStore,
        ITeamTraceStore traceStore,
        AppSettings settings,
        IAppLogger logger)
    {
        _orchestrator = orchestrator;
        _taskStore = taskStore;
        _traceStore = traceStore;
        _settings = settings;
        _logger = logger.ForContext("TeamRun");
    }

    /// <summary>
    /// Runs a Team Task for Chat / schedule. Does not mutate Teams config page state
    /// (no auto-enable, no Team list refresh, no synthetic Team creation).
    /// </summary>
    public async Task<(bool Ok, string Message, TeamTask? Task)> RunTeamGoalAsync(
        string rootSessionId,
        string goal,
        string? teamId = null,
        bool openTrace = false,
        CancellationToken cancellationToken = default,
        IProgress<TeamRunProgress>? progress = null,
        Func<string, string?>? teamNameLookup = null)
    {
        if (!_settings.Teams.Enabled)
        {
            return (false, "Teams disabled", null);
        }

        teamId ??= _settings.Teams.SelectedTeamId;
        if (string.IsNullOrWhiteSpace(teamId))
        {
            return (false, "No team selected", null);
        }

        if (string.IsNullOrWhiteSpace(goal))
        {
            return (false, "Empty goal", null);
        }

        if (string.IsNullOrWhiteSpace(rootSessionId))
        {
            return (false, "Missing root session", null);
        }

        CancelRunForSession(rootSessionId);
        _pendingAsksBySession.Remove(rootSessionId);
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCtsBySession[rootSessionId] = runCts;
        RefreshBusyFromActiveRuns();
        try
        {
            var request = new TeamTaskStartRequest
            {
                TeamId = teamId,
                RootSessionId = rootSessionId,
                Goal = goal.Trim()
            };

            var began = await _orchestrator.BeginAsync(request, runCts.Token).ConfigureAwait(true);
            if (!began.Ok)
            {
                return (false, began.Error ?? "Failed", began.Task);
            }

            // Chat Trace preview bubble at kickoff; full Trace window opens only when requested.
            ReportTeamProgress(progress, new TeamRunProgress
            {
                Kind = TeamRunProgressKind.TaskStarted,
                RootSessionId = began.Task.RootSessionId,
                TaskId = began.Task.TaskId,
                Title = teamNameLookup?.Invoke(began.Task.TeamId) ?? began.Task.TeamId,
                StatusLabel = "Running",
                ResponseText = began.Task.Goal
            });

            if (openTrace)
            {
                TraceRequested?.Invoke(this, (began.Task, live: true));
                // Let the Trace window paint before the Leader turn blocks the UI thread.
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            var completed = await _orchestrator
                .RunToCompletionAsync(
                    began.Task.RootSessionId,
                    began.Task.TaskId,
                    runCts.Token,
                    humanAnswer: null,
                    progress)
                .ConfigureAwait(true);

            return FinalizeTeamRunResult(rootSessionId, completed);
        }
        catch (OperationCanceledException)
        {
            return (false, "cancelled", null);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Team run failed");
            return (false, ex.Message, null);
        }
        finally
        {
            ClearRunCts(rootSessionId, runCts);
            RefreshBusyFromActiveRuns();
        }
    }

    /// <summary>Resumes a Team Task paused on <see cref="TeamTaskStatus.AwaitingHuman"/>.</summary>
    public async Task<(bool Ok, string Message, TeamTask? Task)> ResumeTeamAskAsync(
        string rootSessionId,
        string humanAnswer,
        bool openTrace = false,
        CancellationToken cancellationToken = default,
        IProgress<TeamRunProgress>? progress = null)
    {
        if (!_pendingAsksBySession.TryGetValue(rootSessionId, out var pending))
        {
            return (false, "No pending team question", null);
        }

        if (string.IsNullOrWhiteSpace(humanAnswer))
        {
            return (false, "Empty answer", null);
        }

        CancelRunForSession(rootSessionId);
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCtsBySession[rootSessionId] = runCts;
        RefreshBusyFromActiveRuns();
        try
        {
            if (openTrace)
            {
                var existing = await _taskStore.GetAsync(pending.RootSessionId, pending.TaskId, runCts.Token)
                    .ConfigureAwait(true);
                if (existing is not null)
                {
                    TraceRequested?.Invoke(this, (existing, live: true));
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
            }

            var completed = await _orchestrator
                .RunToCompletionAsync(
                    pending.RootSessionId,
                    pending.TaskId,
                    runCts.Token,
                    humanAnswer.Trim(),
                    progress)
                .ConfigureAwait(true);

            return FinalizeTeamRunResult(rootSessionId, completed);
        }
        catch (OperationCanceledException)
        {
            return (false, "cancelled", null);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Team ask resume failed");
            return (false, ex.Message, null);
        }
        finally
        {
            ClearRunCts(rootSessionId, runCts);
            RefreshBusyFromActiveRuns();
        }
    }

    /// <summary>
    /// Cancels the in-flight Team run for <paramref name="rootSessionId"/> (or all sessions when null).
    /// Returns true when at least one run was cancelled.
    /// </summary>
    public bool CancelActiveTeamRun(string? rootSessionId = null)
    {
        if (!string.IsNullOrWhiteSpace(rootSessionId))
        {
            return CancelRunForSession(rootSessionId);
        }

        var any = false;
        foreach (var sessionId in _runCtsBySession.Keys.ToList())
        {
            any |= CancelRunForSession(sessionId);
        }

        return any;
    }

    public bool IsTeamRunActive(string? rootSessionId = null)
    {
        if (!string.IsNullOrWhiteSpace(rootSessionId))
        {
            return _runCtsBySession.TryGetValue(rootSessionId, out var cts)
                   && !cts.IsCancellationRequested;
        }

        return _runCtsBySession.Values.Any(cts => !cts.IsCancellationRequested);
    }

    public bool TryGetPendingAsk(string rootSessionId, out string taskId)
    {
        if (_pendingAsksBySession.TryGetValue(rootSessionId, out var pending))
        {
            taskId = pending.TaskId;
            return true;
        }

        taskId = "";
        return false;
    }

    public void ClearPendingAsk(string rootSessionId) =>
        _pendingAsksBySession.Remove(rootSessionId);

    public Task<TeamTask?> GetTaskSnapshotAsync(
        string rootSessionId,
        string taskId,
        CancellationToken cancellationToken = default) =>
        _taskStore.GetAsync(rootSessionId, taskId, cancellationToken);

    public IReadOnlyList<ModifiedFileViewModel> CollectChatFiles(TeamTask task) =>
        TeamChatArtifacts.CollectTaskFiles(task, _taskStore);

    public async Task OpenTraceByTaskIdAsync(string rootSessionId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(rootSessionId) || string.IsNullOrWhiteSpace(taskId))
        {
            TraceStatusChanged?.Invoke(this, "无效的 Trace 链接。");
            return;
        }

        var task = await _taskStore.GetAsync(rootSessionId, taskId).ConfigureAwait(true);
        if (task is null)
        {
            // Fall back to latest task in this session when the deep-link target is gone.
            var listed = await _taskStore.ListForRootSessionAsync(rootSessionId).ConfigureAwait(true);
            task = listed.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
                ?? listed.FirstOrDefault();
            if (task is null)
            {
                TraceStatusChanged?.Invoke(this, $"找不到 Team Task：{taskId}");
                return;
            }
        }

        var live = task.Status is TeamTaskStatus.Running or TeamTaskStatus.AwaitingHuman;
        TraceRequested?.Invoke(this, (task, live));
    }

    private void RefreshBusyFromActiveRuns() =>
        BusyChanged?.Invoke(this, IsTeamRunActive());

    private static void ReportTeamProgress(IProgress<TeamRunProgress>? progress, TeamRunProgress update)
    {
        try
        {
            progress?.Report(update);
        }
        catch
        {
            // Chat progress must never fail the Team run.
        }
    }

    private (bool Ok, string Message, TeamTask? Task) FinalizeTeamRunResult(string rootSessionId, TeamTask completed)
    {
        if (completed.Status == TeamTaskStatus.AwaitingHuman)
        {
            _pendingAsksBySession[rootSessionId] = (completed.RootSessionId, completed.TaskId);
            var question = completed.PendingHumanQuestion ?? "Team needs your input.";
            return (false, question, completed);
        }

        _pendingAsksBySession.Remove(rootSessionId);

        var ok = completed.Status == TeamTaskStatus.Succeeded
                 && !string.IsNullOrWhiteSpace(completed.FinalResult);
        var message = ok
            ? completed.FinalResult!
            : completed.Error
              ?? (completed.Status == TeamTaskStatus.Succeeded
                  ? "Succeeded without FinalResult"
                  : completed.Status.ToString());
        return (ok, message, completed);
    }

    private bool CancelRunForSession(string rootSessionId)
    {
        if (!_runCtsBySession.TryGetValue(rootSessionId, out var cts)
            || cts.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void ClearRunCts(string rootSessionId, CancellationTokenSource runCts)
    {
        if (_runCtsBySession.TryGetValue(rootSessionId, out var current)
            && ReferenceEquals(current, runCts))
        {
            _runCtsBySession.Remove(rootSessionId);
        }

        runCts.Dispose();
    }
}
