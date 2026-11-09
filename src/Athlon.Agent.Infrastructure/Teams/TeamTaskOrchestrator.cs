using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

public sealed class TeamTaskOrchestrator(
    AppSettings settings,
    ITeamDefinitionStore teamStore,
    IAgentProfileStore profileStore,
    ITeamTaskStore taskStore,
    ITeamTraceStore traceStore,
    ITeamMemoryStore memoryStore,
    ITeamMemberRunExecutor memberExecutor,
    IFileStorageService storage,
    IAppLogger logger) : ITeamTaskOrchestrator
{
    private readonly IAppLogger _logger = logger.ForContext("TeamTaskOrchestrator");

    public async Task<TeamTaskStartResult> BeginAsync(
        TeamTaskStartRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return TeamTaskStartResult.Fail("Teams feature is disabled.");
        }

        var team = await teamStore.GetAsync(request.TeamId, cancellationToken).ConfigureAwait(false);
        if (team is null)
        {
            return TeamTaskStartResult.Fail($"Team '{request.TeamId}' not found.");
        }

        var leader = await profileStore.GetAsync(team.LeaderProfileId, cancellationToken).ConfigureAwait(false);
        if (leader is null)
        {
            return TeamTaskStartResult.Fail($"Leader profile '{team.LeaderProfileId}' not found.");
        }

        var task = new TeamTask
        {
            TaskId = "tm_" + IdGen.NewId()[..10],
            TeamId = team.Id,
            RootSessionId = request.RootSessionId,
            Goal = request.Goal.Trim(),
            Status = TeamTaskStatus.Running,
            LeaderProfileId = leader.Id,
            // Stable across Team Tasks in the same Chat session so multi-turn Teams keep one Leader thread.
            LeaderSessionId = taskStore.StableLeaderSessionId(team.Id),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await SaveLockedAsync(task, cancellationToken).ConfigureAwait(false);
        await traceStore.AppendAsync(
            task.RootSessionId,
            task.TaskId,
            new TeamTraceEvent { Type = "task.started", NodeId = "leader", Message = task.Goal },
            cancellationToken).ConfigureAwait(false);

        _logger.Information("Team task started id={TaskId} team={TeamId}", task.TaskId, team.Id);
        return TeamTaskStartResult.Success(task, "started");
    }

    public async Task<TeamTaskStartResult> StartAsync(
        TeamTaskStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var began = await BeginAsync(request, cancellationToken).ConfigureAwait(false);
        if (!began.Ok)
        {
            return began;
        }

        var completed = await RunToCompletionAsync(
                began.Task.RootSessionId,
                began.Task.TaskId,
                cancellationToken,
                humanAnswer: null,
                progress: null)
            .ConfigureAwait(false);
        return TeamTaskStartResult.Success(
            completed,
            completed.FinalResult ?? completed.Error ?? "Team task finished.");
    }

    public Task<TeamTask?> GetTaskAsync(string rootSessionId, string taskId, CancellationToken cancellationToken = default) =>
        taskStore.GetAsync(rootSessionId, taskId, cancellationToken);

    public async Task<TeamTask> RunToCompletionAsync(
        string rootSessionId,
        string taskId,
        CancellationToken cancellationToken = default,
        string? humanAnswer = null,
        IProgress<TeamRunProgress>? progress = null)
    {
        var team = await LoadTeamForTaskAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false);
        var maxParallel = ResolveMaxParallel(team);

        AgentProfile leader;
        string leaderSessionId;
        string leaderKickoff;

        using (await TeamTaskLock.AcquireAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false))
        {
            var task = await taskStore.GetAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Team task '{taskId}' not found.");

            leader = await profileStore.GetAsync(task.LeaderProfileId ?? "", cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Leader profile missing.");

            leaderSessionId = task.LeaderSessionId ?? ("ldr_" + IdGen.NewId()[..10]);
            task.LeaderSessionId = leaderSessionId;
            if (task.Status is TeamTaskStatus.AwaitingHuman && !string.IsNullOrWhiteSpace(humanAnswer))
            {
                task.Status = TeamTaskStatus.Running;
                task.PendingHumanQuestion = null;
            }
            else if (task.Status is not TeamTaskStatus.Succeeded and not TeamTaskStatus.Failed and not TeamTaskStatus.Cancelled)
            {
                task.Status = TeamTaskStatus.Running;
            }

            await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
            leaderKickoff = string.IsNullOrWhiteSpace(humanAnswer)
                ? await BuildLeaderKickoffAsync(task, cancellationToken).ConfigureAwait(false)
                : await BuildLeaderResumeAsync(task, humanAnswer!, cancellationToken).ConfigureAwait(false);
        }

        var inFlight = new Dictionary<string, Task<WorkerRunResult>>(StringComparer.OrdinalIgnoreCase);
        var needsLeader = true;
        var useInitialKickoff = true;
        var emptyStopNudgesUsed = 0;
        var leaderTurns = 0;
        var maxLeaderTurns = Math.Max(1, settings.Teams.MaxLeaderTurnsPerTask);
        var maxEmptyNudges = Math.Max(1, settings.Teams.MaxEmptyStopNudges);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Phase 1: start queued workers up to parallel cap.
                using (await TeamTaskLock.AcquireAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false))
                {
                    var task = await taskStore.GetAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Team task '{taskId}' not found.");

                    if (TeamWorkerHelpers.IsTerminal(task.Status) || task.Status == TeamTaskStatus.AwaitingHuman)
                    {
                        return task;
                    }

                    TeamWorkerHelpers.RefreshReady(task);
                    // Prefer in-flight count over disk Running so orphaned Running rows
                    // (e.g. after process restart) cannot starve the queue forever.
                    foreach (var orphan in task.SubTasks.Where(s =>
                                 s.Status == TeamSubTaskStatus.Running && !inFlight.ContainsKey(s.Id)))
                    {
                        orphan.Status = TeamSubTaskStatus.Queued;
                    }

                    var runningCount = inFlight.Count;
                    foreach (var sub in task.SubTasks.Where(s => s.Status == TeamSubTaskStatus.Queued).ToList())
                    {
                        if (inFlight.ContainsKey(sub.Id) || runningCount >= maxParallel)
                        {
                            continue;
                        }

                        var started = await TryStartWorkerAsync(task, sub, cancellationToken).ConfigureAwait(false);
                        if (started is not null)
                        {
                            inFlight[sub.Id] = started;
                            runningCount++;
                        }
                    }

                    await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
                }

                // Phase 2: wait for any in-flight worker (no lock held).
                if (inFlight.Count > 0)
                {
                    var finished = await WaitForAnyWorkerAsync(inFlight.Values, cancellationToken).ConfigureAwait(false);
                    inFlight.Remove(finished.SubTaskId);
                    await ApplyWorkerResultAsync(rootSessionId, taskId, finished, progress).ConfigureAwait(false);
                    needsLeader = true;
                    continue;
                }

                // Phase 3: decide Leader turn vs finalize.
                TeamTask snapshot;
                using (await TeamTaskLock.AcquireAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false))
                {
                    snapshot = await taskStore.GetAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Team task '{taskId}' not found.");

                    if (TeamWorkerHelpers.IsTerminal(snapshot.Status) || snapshot.Status == TeamTaskStatus.AwaitingHuman)
                    {
                        return snapshot;
                    }

                    if (snapshot.SubTasks.Any(s => s.Status == TeamSubTaskStatus.Queued) && inFlight.Count > 0)
                    {
                        // Slot was full earlier; wait for capacity then start more.
                        continue;
                    }

                    if (snapshot.SubTasks.Any(s => s.Status == TeamSubTaskStatus.Queued) && inFlight.Count == 0)
                    {
                        // Orphaned Running on disk blocked starts; reclaim then retry.
                        foreach (var orphan in snapshot.SubTasks.Where(s =>
                                     s.Status == TeamSubTaskStatus.Running && !inFlight.ContainsKey(s.Id)))
                        {
                            orphan.Status = TeamSubTaskStatus.Queued;
                        }

                        await taskStore.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
                        continue;
                    }

                    if (!needsLeader)
                    {
                        // Finalize while still holding the lock (SaveLocked would deadlock).
                        return await FinalizeIfIncompleteCoreAsync(snapshot).ConfigureAwait(false);
                    }

                    if (leaderTurns >= maxLeaderTurns)
                    {
                        snapshot.Status = TeamTaskStatus.Failed;
                        snapshot.Error =
                            $"MaxLeaderTurnsPerTask={maxLeaderTurns} reached. Ask the user or submit a partial/failed result next time.";
                        snapshot.CompletedAt = DateTimeOffset.UtcNow;
                        await taskStore.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
                        await traceStore.AppendAsync(
                            snapshot.RootSessionId,
                            snapshot.TaskId,
                            new TeamTraceEvent { Type = "task.failed", Message = snapshot.Error },
                            CancellationToken.None).ConfigureAwait(false);
                        return snapshot;
                    }

                    // Always rebuild follow-up from latest disk state after the first kickoff/resume.
                    if (!useInitialKickoff)
                    {
                        leaderKickoff = BuildLeaderFollowUp(snapshot);
                    }
                }

                await RunLeaderTurnAsync(
                        rootSessionId,
                        taskId,
                        leader,
                        leaderSessionId,
                        leaderKickoff,
                        cancellationToken,
                        progress)
                    .ConfigureAwait(false);

                leaderTurns++;
                useInitialKickoff = false;

                using (await TeamTaskLock.AcquireAsync(rootSessionId, taskId, CancellationToken.None).ConfigureAwait(false))
                {
                    snapshot = await taskStore.GetAsync(rootSessionId, taskId, CancellationToken.None).ConfigureAwait(false)
                        ?? snapshot;
                    if (TeamWorkerHelpers.IsTerminal(snapshot.Status) || snapshot.Status == TeamTaskStatus.AwaitingHuman)
                    {
                        return snapshot;
                    }

                    // Empty-stop safety: if Leader ended with actionable Ready/Failed and nothing in flight,
                    // inject a stronger nudge turn instead of Incomplete Failed immediately.
                    if (emptyStopNudgesUsed < maxEmptyNudges
                        && snapshot.Status == TeamTaskStatus.Running
                        && string.IsNullOrWhiteSpace(snapshot.FinalResult)
                        && HasActionableReadyOrFailed(snapshot)
                        && !snapshot.SubTasks.Any(s =>
                            s.Status is TeamSubTaskStatus.Queued or TeamSubTaskStatus.Running))
                    {
                        emptyStopNudgesUsed++;
                        needsLeader = true;
                        leaderKickoff = BuildLeaderEmptyStopNudge(snapshot);
                    }
                    else
                    {
                        leaderKickoff = BuildLeaderFollowUp(snapshot);
                        needsLeader = false;
                    }
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await DrainInFlightAsync(inFlight, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return await CancelTaskAsync(rootSessionId, taskId).ConfigureAwait(false);
        }
    }

    private static async Task<WorkerRunResult> WaitForAnyWorkerAsync(
        IEnumerable<Task<WorkerRunResult>> workers,
        CancellationToken cancellationToken)
    {
        var whenAny = Task.WhenAny(workers);
        var completed = await whenAny.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await completed.ConfigureAwait(false);
    }

    private static async Task DrainInFlightAsync(
        Dictionary<string, Task<WorkerRunResult>> inFlight,
        TimeSpan timeout)
    {
        if (inFlight.Count == 0)
        {
            return;
        }

        try
        {
            var all = Task.WhenAll(inFlight.Values.ToArray());
            await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: give workers a brief window to observe cancel before we mark Cancelled.
        }

        inFlight.Clear();
    }

    private async Task RunLeaderTurnAsync(
        string rootSessionId,
        string taskId,
        AgentProfile leader,
        string leaderSessionId,
        string kickoff,
        CancellationToken cancellationToken,
        IProgress<TeamRunProgress>? progress)
    {
        TeamTask task;
        using (await TeamTaskLock.AcquireAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false))
        {
            task = await taskStore.GetAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Team task '{taskId}' not found.");
        }

        using (TeamTaskExecutionScope.Enter(task))
        {
            var outcome = await memberExecutor.ExecuteAsync(
                task,
                leader,
                leaderSessionId,
                kickoff,
                isLeader: true,
                cancellationToken).ConfigureAwait(false);

            using (await TeamTaskLock.AcquireAsync(rootSessionId, taskId, CancellationToken.None).ConfigureAwait(false))
            {
                var latest = await taskStore.GetAsync(rootSessionId, taskId, CancellationToken.None).ConfigureAwait(false)
                    ?? task;
                var scoped = TeamTaskExecutionScope.Current?.Task ?? task;
                MergeLeaderMutations(latest, scoped);

                // Leader text Ok is secondary to tool side effects (dispatch / ask_user / submit).
                if (!outcome.Ok && !LeaderTurnHasUsefulSideEffects(latest))
                {
                    latest.Status = TeamTaskStatus.Failed;
                    latest.Error = outcome.Error ?? outcome.ResponseText;
                    latest.CompletedAt = DateTimeOffset.UtcNow;
                    await taskStore.SaveAsync(latest, CancellationToken.None).ConfigureAwait(false);
                    await traceStore.AppendAsync(
                        latest.RootSessionId,
                        latest.TaskId,
                        new TeamTraceEvent { Type = "task.failed", Message = latest.Error },
                        CancellationToken.None).ConfigureAwait(false);
                    ReportProgress(progress, new TeamRunProgress
                    {
                        Kind = TeamRunProgressKind.LeaderTurnCompleted,
                        RootSessionId = rootSessionId,
                        TaskId = taskId,
                        Title = "Leader",
                        ProfileId = leader.Id,
                        StatusLabel = "Failed",
                        ResponseText = outcome.ResponseText,
                        Error = latest.Error
                    });
                    return;
                }

                if (!outcome.Ok)
                {
                    await traceStore.AppendAsync(
                        latest.RootSessionId,
                        latest.TaskId,
                        new TeamTraceEvent
                        {
                            Type = "leader.parse_ignored",
                            NodeId = "leader",
                            Message = outcome.Error ?? "Leader text non-success; keeping tool side effects."
                        },
                        CancellationToken.None).ConfigureAwait(false);
                }

                await taskStore.SaveAsync(latest, CancellationToken.None).ConfigureAwait(false);
                ReportProgress(progress, new TeamRunProgress
                {
                    Kind = TeamRunProgressKind.LeaderTurnCompleted,
                    RootSessionId = rootSessionId,
                    TaskId = taskId,
                    Title = "Leader",
                    ProfileId = leader.Id,
                    StatusLabel = latest.Status == TeamTaskStatus.AwaitingHuman
                        ? "AwaitingHuman"
                        : latest.Status == TeamTaskStatus.Succeeded ? "Succeeded" : "Running",
                    ResponseText = string.IsNullOrWhiteSpace(latest.FinalResult)
                        ? outcome.ResponseText
                        : latest.FinalResult!
                });
            }
        }
    }

    private static bool LeaderTurnHasUsefulSideEffects(TeamTask task) =>
        task.Status == TeamTaskStatus.AwaitingHuman
        || (task.Status == TeamTaskStatus.Succeeded && !string.IsNullOrWhiteSpace(task.FinalResult))
        || task.SubTasks.Any(s =>
            s.Status is TeamSubTaskStatus.Queued
                or TeamSubTaskStatus.Ready
                or TeamSubTaskStatus.Running
                or TeamSubTaskStatus.Pending);

    private static void MergeLeaderMutations(TeamTask disk, TeamTask scoped)
    {
        if (scoped.Status is TeamTaskStatus.Succeeded or TeamTaskStatus.AwaitingHuman or TeamTaskStatus.Failed)
        {
            disk.Status = scoped.Status;
        }

        if (!string.IsNullOrWhiteSpace(scoped.FinalResult))
        {
            disk.FinalResult = scoped.FinalResult;
            disk.CompletedAt = scoped.CompletedAt ?? disk.CompletedAt;
        }

        disk.PendingHumanQuestion = scoped.PendingHumanQuestion;
        disk.RepairRoundsUsed = Math.Max(disk.RepairRoundsUsed, scoped.RepairRoundsUsed);
        if (!string.IsNullOrWhiteSpace(scoped.Error) && disk.Status == TeamTaskStatus.Failed)
        {
            disk.Error = scoped.Error;
        }

        foreach (var scopedSub in scoped.SubTasks)
        {
            var diskSub = disk.SubTasks.FirstOrDefault(s =>
                string.Equals(s.Id, scopedSub.Id, StringComparison.OrdinalIgnoreCase));
            if (diskSub is null)
            {
                disk.SubTasks.Add(CloneSub(scopedSub));
                continue;
            }

            // Never let a stale Leader snapshot rewind terminal / running workers.
            if (IsProtectedWorkerStatus(diskSub.Status) && IsLeaderQueueStatus(scopedSub.Status))
            {
                continue;
            }

            if (scopedSub.Status is TeamSubTaskStatus.Queued
                or TeamSubTaskStatus.Ready
                or TeamSubTaskStatus.Pending
                or TeamSubTaskStatus.WaitingRedispatch)
            {
                diskSub.Status = scopedSub.Status;
            }

            diskSub.Title = scopedSub.Title;
            diskSub.Handoff = scopedSub.Handoff;
            diskSub.DependsOn = scopedSub.DependsOn.ToList();
            diskSub.Iteration = scopedSub.Iteration;
#pragma warning disable CS0618
            diskSub.MaxIterations = scopedSub.MaxIterations;
#pragma warning restore CS0618
            diskSub.RedispatchReason = scopedSub.RedispatchReason;
            if (scopedSub.Status == TeamSubTaskStatus.Queued
                && !string.IsNullOrWhiteSpace(scopedSub.MemberSessionId))
            {
                diskSub.MemberSessionId = scopedSub.MemberSessionId;
            }
            else
            {
                diskSub.MemberSessionId ??= scopedSub.MemberSessionId;
            }

            diskSub.AssigneeProfileId = scopedSub.AssigneeProfileId;
            if (!string.IsNullOrWhiteSpace(scopedSub.PrevResultSummary))
            {
                diskSub.PrevResultSummary = scopedSub.PrevResultSummary;
            }
        }
    }

    private static TeamSubTask CloneSub(TeamSubTask s) => new()
    {
        Id = s.Id,
        Title = s.Title,
        AssigneeProfileId = s.AssigneeProfileId,
        MemberSessionId = s.MemberSessionId,
        DependsOn = s.DependsOn.ToList(),
        Status = s.Status,
        Handoff = s.Handoff,
        ArtifactRefs = s.ArtifactRefs.ToList(),
        Iteration = s.Iteration,
#pragma warning disable CS0618
        MaxIterations = s.MaxIterations,
#pragma warning restore CS0618
        RedispatchReason = s.RedispatchReason,
        PrevResultSummary = s.PrevResultSummary,
        Result = s.Result,
        Error = s.Error,
        StartedAt = s.StartedAt,
        CompletedAt = s.CompletedAt
    };

    private async Task<TeamTask> FinalizeIfIncompleteCoreAsync(TeamTask task)
    {
        if (task.Status == TeamTaskStatus.Running && string.IsNullOrWhiteSpace(task.FinalResult))
        {
            if (task.SubTasks.Count > 0
                && task.SubTasks.All(s => TeamWorkerHelpers.IsWorkerFinished(s.Status)))
            {
                task.FinalResult = AggregateResults(task);
                task.Status = task.SubTasks.Any(s => s.Status == TeamSubTaskStatus.Failed)
                    ? TeamTaskStatus.Failed
                    : TeamTaskStatus.Succeeded;
                task.CompletedAt = DateTimeOffset.UtcNow;
            }
            else if (task.SubTasks.Any(s =>
                         s.Status is TeamSubTaskStatus.Ready or TeamSubTaskStatus.Pending or TeamSubTaskStatus.Queued))
            {
                task.Status = TeamTaskStatus.Failed;
                task.Error = "Incomplete: Leader finished with pending/ready work and no workers running.";
                task.CompletedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                task.Status = TeamTaskStatus.Failed;
                task.Error = "Incomplete: Leader did not submit a final result.";
                task.CompletedAt = DateTimeOffset.UtcNow;
            }

            await taskStore.SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
        }

        return task;
    }

    private async Task<TeamDefinition?> LoadTeamForTaskAsync(
        string rootSessionId,
        string taskId,
        CancellationToken cancellationToken)
    {
        var task = await taskStore.GetAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return null;
        }

        return await teamStore.GetAsync(task.TeamId, cancellationToken).ConfigureAwait(false);
    }

    private int ResolveMaxParallel(TeamDefinition? team)
    {
        var fromSettings = Math.Max(1, settings.Teams.MaxParallelWorkers);
        var fromTeam = team?.MaxParallelWorkers ?? 0;
        if (fromTeam <= 0)
        {
            return fromSettings;
        }

        return Math.Max(1, Math.Min(fromTeam, fromSettings));
    }

    private async Task<Task<WorkerRunResult>?> TryStartWorkerAsync(
        TeamTask task,
        TeamSubTask sub,
        CancellationToken cancellationToken)
    {
        var profile = await profileStore.GetAsync(sub.AssigneeProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            sub.Status = TeamSubTaskStatus.Failed;
            sub.Error = "profile missing";
            sub.CompletedAt = DateTimeOffset.UtcNow;
            await traceStore.AppendAsync(
                task.RootSessionId,
                task.TaskId,
                new TeamTraceEvent
                {
                    Type = "worker.failed",
                    NodeId = sub.Id,
                    SubTaskId = sub.Id,
                    Message = "profile missing"
                },
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        sub.MemberSessionId ??= "mbr_" + IdGen.NewId()[..10];
        sub.Status = TeamSubTaskStatus.Running;
        sub.StartedAt ??= DateTimeOffset.UtcNow;
        sub.CompletedAt = null;

        await traceStore.AppendAsync(
            task.RootSessionId,
            task.TaskId,
            new TeamTraceEvent
            {
                Type = string.IsNullOrWhiteSpace(sub.RedispatchReason) ? "worker.dispatch" : "worker.redispatch",
                NodeId = sub.Id,
                FromNodeId = "leader",
                ToNodeId = sub.Id,
                ProfileId = profile.Id,
                SubTaskId = sub.Id,
                Iteration = sub.Iteration,
                Message = sub.RedispatchReason
            },
            cancellationToken).ConfigureAwait(false);

        var message = TeamWorkerHelpers.BuildWorkerMessage(task, sub);
        var memberSessionId = sub.MemberSessionId!;
        var subId = sub.Id;
        var profileId = profile.Id;
        var iteration = sub.Iteration;
        var taskForExec = task;
        var timeoutSeconds = Math.Max(30, settings.Teams.WorkerRunTimeoutSeconds);

        return Task.Run(async () =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                var outcome = await memberExecutor.ExecuteAsync(
                    taskForExec,
                    profile,
                    memberSessionId,
                    message,
                    isLeader: false,
                    timeoutCts.Token).ConfigureAwait(false);
                return new WorkerRunResult(subId, profileId, iteration, outcome);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new WorkerRunResult(
                    subId,
                    profileId,
                    iteration,
                    TeamMemberRunOutcome.Fail("cancelled"),
                    Cancelled: true);
            }
            catch (OperationCanceledException)
            {
                return new WorkerRunResult(
                    subId,
                    profileId,
                    iteration,
                    TeamMemberRunOutcome.Fail($"Worker timed out after {timeoutSeconds}s"),
                    TimedOut: true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Worker run crashed sub={SubTaskId}", subId);
                return new WorkerRunResult(subId, profileId, iteration, TeamMemberRunOutcome.Fail(ex.Message));
            }
        }, cancellationToken);
    }

    private async Task ApplyWorkerResultAsync(
        string rootSessionId,
        string taskId,
        WorkerRunResult result,
        IProgress<TeamRunProgress>? progress)
    {
        using (await TeamTaskLock.AcquireAsync(rootSessionId, taskId, CancellationToken.None).ConfigureAwait(false))
        {
            var task = await taskStore.GetAsync(rootSessionId, taskId, CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Team task '{taskId}' not found.");
            var sub = task.SubTasks.FirstOrDefault(s =>
                string.Equals(s.Id, result.SubTaskId, StringComparison.OrdinalIgnoreCase));
            if (sub is null)
            {
                return;
            }

            sub.CompletedAt = DateTimeOffset.UtcNow;
            TeamRunProgressKind kind;
            if (result.Cancelled)
            {
                sub.Status = TeamSubTaskStatus.Cancelled;
                sub.Error = "cancelled";
                kind = TeamRunProgressKind.WorkerCancelled;
            }
            else if (result.TimedOut)
            {
                sub.Status = TeamSubTaskStatus.Failed;
                sub.Error = result.Outcome.Error ?? "worker timed out";
                kind = TeamRunProgressKind.WorkerFailed;
            }
            else if (result.Outcome.Ok)
            {
                sub.Status = TeamSubTaskStatus.Succeeded;
                sub.Result = result.Outcome.ResponseText;
                sub.PrevResultSummary = TeamWorkerHelpers.Truncate(result.Outcome.ResponseText, 1200);
                sub.Error = null;
                kind = TeamRunProgressKind.WorkerCompleted;
            }
            else
            {
                sub.Status = TeamSubTaskStatus.Failed;
                kind = TeamRunProgressKind.WorkerFailed;
                // Keep full worker text when present so Leader can collect verification findings.
                if (!string.IsNullOrWhiteSpace(result.Outcome.ResponseText)
                    && !string.Equals(
                        result.Outcome.ResponseText,
                        result.Outcome.Error,
                        StringComparison.Ordinal))
                {
                    sub.Result = result.Outcome.ResponseText;
                    sub.PrevResultSummary = TeamWorkerHelpers.Truncate(result.Outcome.ResponseText, 1200);
                }

                sub.Error = result.Outcome.Error ?? result.Outcome.ResponseText;
            }

            await traceStore.AppendAsync(
                task.RootSessionId,
                task.TaskId,
                new TeamTraceEvent
                {
                    Type = result.Cancelled
                        ? "worker.cancelled"
                        : result.Outcome.Ok ? "worker.completed" : "worker.failed",
                    NodeId = sub.Id,
                    ProfileId = result.ProfileId,
                    SubTaskId = sub.Id,
                    Iteration = result.Iteration,
                    Message = TeamWorkerHelpers.Truncate(result.Outcome.ResponseText)
                },
                CancellationToken.None).ConfigureAwait(false);

            if (settings.Teams.AutoWriteTeamMemory && !result.Cancelled)
            {
                try
                {
                    var parsed = TeamMemberResultParser.Parse(result.Outcome.ResponseText);
                    var keyPrefix = result.Outcome.Ok ? "subtask" : "verify";
                    var key = $"{keyPrefix}:{sub.Id}:result";
                    if (!result.Outcome.Ok && parsed.Status == TeamMemberResultStatus.Failed)
                    {
                        key = $"verify:{sub.Id}:findings";
                    }

                    await memoryStore.PutAsync(
                        task.RootSessionId,
                        task.TaskId,
                        new TeamMemoryEntry
                        {
                            Key = key,
                            Content = TeamWorkerHelpers.Truncate(
                                result.Outcome.ResponseText ?? result.Outcome.Error,
                                ITeamMemoryStore.MaxContentChars),
                            Tags = result.Outcome.Ok
                                ? ["subtask", "result", "auto"]
                                : ["subtask", "failed", "auto"],
                            AuthorProfileId = result.ProfileId
                        },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        "Failed to auto-write team memory for {SubTaskId}: {Message}",
                        sub.Id,
                        ex.Message);
                }
            }

            TeamWorkerHelpers.RefreshReady(task);
            await taskStore.SaveAsync(task, CancellationToken.None).ConfigureAwait(false);

            var responseText = !string.IsNullOrWhiteSpace(sub.Result)
                ? sub.Result!
                : result.Outcome.ResponseText;
            if (string.IsNullOrWhiteSpace(responseText) && !string.IsNullOrWhiteSpace(sub.Error))
            {
                responseText = sub.Error!;
            }

            ReportProgress(progress, new TeamRunProgress
            {
                Kind = kind,
                RootSessionId = rootSessionId,
                TaskId = taskId,
                SubTaskId = sub.Id,
                Title = string.IsNullOrWhiteSpace(sub.Title) ? sub.Id : sub.Title,
                ProfileId = result.ProfileId ?? sub.AssigneeProfileId,
                Iteration = result.Iteration > 0 ? result.Iteration : sub.Iteration,
                StatusLabel = sub.Status.ToString(),
                ResponseText = responseText ?? "",
                Error = sub.Error
            });
        }
    }

    private static void ReportProgress(IProgress<TeamRunProgress>? progress, TeamRunProgress update)
    {
        try
        {
            progress?.Report(update);
        }
        catch
        {
            // Chat UI progress must never fail the Team run.
        }
    }

    private async Task<TeamTask> CancelTaskAsync(string rootSessionId, string taskId)
    {
        using (await TeamTaskLock.AcquireAsync(rootSessionId, taskId, CancellationToken.None).ConfigureAwait(false))
        {
            var task = await taskStore.GetAsync(rootSessionId, taskId, CancellationToken.None).ConfigureAwait(false)
                ?? new TeamTask { RootSessionId = rootSessionId, TaskId = taskId };
            task.Status = TeamTaskStatus.Cancelled;
            task.Error = "cancelled";
            task.CompletedAt = DateTimeOffset.UtcNow;
            foreach (var sub in task.SubTasks.Where(s =>
                         s.Status is TeamSubTaskStatus.Running or TeamSubTaskStatus.Queued or TeamSubTaskStatus.Ready))
            {
                sub.Status = TeamSubTaskStatus.Cancelled;
                sub.Error = "cancelled";
                sub.CompletedAt = DateTimeOffset.UtcNow;
                await traceStore.AppendAsync(
                    task.RootSessionId,
                    task.TaskId,
                    new TeamTraceEvent
                    {
                        Type = "worker.cancelled",
                        NodeId = sub.Id,
                        SubTaskId = sub.Id,
                        Message = "cancelled by user"
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }

            await taskStore.SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
            await traceStore.AppendAsync(
                task.RootSessionId,
                task.TaskId,
                new TeamTraceEvent { Type = "task.cancelled", Message = "cancelled by user" },
                CancellationToken.None).ConfigureAwait(false);
            return task;
        }
    }

    private async Task SaveLockedAsync(TeamTask task, CancellationToken cancellationToken)
    {
        using (await TeamTaskLock.AcquireAsync(task.RootSessionId, task.TaskId, cancellationToken).ConfigureAwait(false))
        {
            await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> BuildLeaderKickoffAsync(TeamTask task, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- TeamTask boundary: {task.TaskId} ---");
        sb.AppendLine("Organize the team to complete this goal.");
        sb.AppendLine(task.Goal);
        sb.AppendLine();
        sb.AppendLine(
            "This Leader conversation is shared for the Team inside this Chat session — "
            + "treat prior turns in this thread as continuous context.");
        sb.AppendLine(
            $"New Team Task id={task.TaskId}. Subtasks and team_memory start empty for this task "
            + "(do not assume prior-task blackboard keys still exist).");
        AppendChatSessionContext(sb, await TryLoadRootSessionAsync(task.RootSessionId, cancellationToken).ConfigureAwait(false));
        sb.AppendLine("Scheduling policy (important):");
        sb.AppendLine("- Workers are a capability pool — do NOT start every Worker by default.");
        sb.AppendLine("- Choose only the Workers whose skills match this goal; start with the smallest useful set (often 1).");
        sb.AppendLine("- After results return, decide the next Worker(s) yourself (or redispatch / ask_user / submit).");
        sb.AppendLine("- Prefer sequential waves when later work depends on earlier findings; parallelize only independent slices.");
        sb.AppendLine();
        sb.AppendLine("Recommended flow:");
        sb.AppendLine("1) team_list_members — inspect the pool");
        sb.AppendLine("2) team_create_subtask only for Workers you need now (depends_on must use real subtask ids)");
        sb.AppendLine("3) team_dispatch those Ready subtasks");
        sb.AppendLine("4) STOP — do not poll; the outer loop re-invokes you when workers finish");
        sb.AppendLine("5) On re-invoke: team_collect / team_memory / team_progress; then schedule the next Worker, redispatch, ask_user, or team_submit_result");
        sb.AppendLine(
            "You may redispatch / create repair subtasks while repair rounds remain "
            + $"(MaxRepairRounds={Math.Max(1, settings.Teams.MaxRepairRounds)}). There is no per-worker MaxIterations.");
        return sb.ToString();
    }

    private string BuildLeaderFollowUp(TeamTask task)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Continue the team task. Schedule the next step from results — do not auto-engage idle Workers.");
        sb.AppendLine("Current subtask status:");
        foreach (var sub in task.SubTasks)
        {
            sb.AppendLine(
                $"- {sub.Id} [{sub.Status}] attempt={sub.Iteration} {sub.Title}");
        }

        sb.AppendLine();
        sb.AppendLine(
            $"Repair rounds used: {task.RepairRoundsUsed}/{Math.Max(1, settings.Teams.MaxRepairRounds)}");
        var failed = task.SubTasks.Where(s => s.Status == TeamSubTaskStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine(
                "One or more subtasks Failed (often verification). Use team_collect / team_memory, then "
                + "team_redispatch (with handoff findings) or create a targeted fix subtask + dispatch — while repair rounds remain. "
                + "Otherwise team_ask_user or submit an explicit partial/failed FinalResult.");
        }
        else if (task.SubTasks.Any(s => s.Status is TeamSubTaskStatus.Ready or TeamSubTaskStatus.Queued))
        {
            sb.AppendLine("Dispatch remaining Ready/Queued work you intentionally created, then STOP.");
        }
        else if (task.SubTasks.Count == 0)
        {
            sb.AppendLine("No subtasks yet. Pick the smallest useful Worker set for the goal, create + dispatch, then STOP.");
        }
        else
        {
            sb.AppendLine(
                "Review finished work. If the goal is met: team_collect then team_submit_result. "
                + "If more specialized help is needed: create/dispatch only that Worker. Do not blind-redispatch or fan-out to unused Workers.");
        }

        return sb.ToString();
    }

    private string BuildLeaderEmptyStopNudge(TeamTask task)
    {
        var sb = new StringBuilder();
        sb.AppendLine("URGENT: you stopped without dispatching / redistributing / submitting, but actionable work remains.");
        sb.AppendLine("You MUST call one of: team_dispatch, team_redispatch, team_create_subtask+team_dispatch, team_submit_result, or team_ask_user.");
        sb.AppendLine("Do not reply with text only.");
        sb.AppendLine();
        sb.AppendLine(BuildLeaderFollowUp(task));
        return sb.ToString();
    }

    private static bool HasActionableReadyOrFailed(TeamTask task) =>
        task.SubTasks.Any(s => s.Status is TeamSubTaskStatus.Ready or TeamSubTaskStatus.Failed);

    private async Task<string> BuildLeaderResumeAsync(
        TeamTask task,
        string humanAnswer,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The human answered your question. Continue the team task.");
        sb.AppendLine("Human answer:");
        sb.AppendLine(humanAnswer.Trim());
        sb.AppendLine();
        AppendChatSessionContext(sb, await TryLoadRootSessionAsync(task.RootSessionId, cancellationToken).ConfigureAwait(false));
        sb.AppendLine(BuildLeaderFollowUp(task));
        return sb.ToString();
    }

    private async Task<AgentSession?> TryLoadRootSessionAsync(string rootSessionId, CancellationToken cancellationToken)
    {
        try
        {
            return await storage.LoadSessionAsync(rootSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed loading Chat session for Team kickoff context: {Error}", ex.Message);
            return null;
        }
    }

    private static void AppendChatSessionContext(StringBuilder sb, AgentSession? rootSession)
    {
        if (rootSession?.Messages is not { Count: > 0 })
        {
            return;
        }

        // Keep a short tail of Chat turns so multi-wave Teams stays aligned with the user thread.
        const int maxMessages = 8;
        const int maxCharsPerMessage = 600;
        var recent = rootSession.Messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant)
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Where(m => !m.Content.TrimStart().StartsWith("### Teams ·", StringComparison.Ordinal))
            .TakeLast(maxMessages)
            .ToList();
        if (recent.Count == 0)
        {
            return;
        }

        sb.AppendLine("Recent Chat session context (same conversation; stay consistent with it):");
        foreach (var message in recent)
        {
            var role = message.Role == MessageRole.User ? "user" : "assistant";
            var text = TeamWorkerHelpers.Truncate(message.Content, maxCharsPerMessage);
            sb.AppendLine($"- [{role}] {text}");
        }

        sb.AppendLine();
    }

    private static string AggregateResults(TeamTask task)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Team result for: {task.Goal}");
        foreach (var sub in task.SubTasks)
        {
            sb.AppendLine();
            sb.AppendLine($"### {sub.Title} ({sub.Status})");
            sb.AppendLine(TeamWorkerHelpers.Truncate(sub.Result ?? sub.Error ?? "(empty)", 1200));
        }

        return sb.ToString();
    }

    private static bool IsProtectedWorkerStatus(TeamSubTaskStatus status) =>
        status is TeamSubTaskStatus.Succeeded
            or TeamSubTaskStatus.Failed
            or TeamSubTaskStatus.Cancelled
            or TeamSubTaskStatus.Running;

    private static bool IsLeaderQueueStatus(TeamSubTaskStatus status) =>
        status is TeamSubTaskStatus.Queued
            or TeamSubTaskStatus.Ready
            or TeamSubTaskStatus.Pending
            or TeamSubTaskStatus.WaitingRedispatch;

    private sealed record WorkerRunResult(
        string SubTaskId,
        string ProfileId,
        int Iteration,
        TeamMemberRunOutcome Outcome,
        bool Cancelled = false,
        bool TimedOut = false);
}
