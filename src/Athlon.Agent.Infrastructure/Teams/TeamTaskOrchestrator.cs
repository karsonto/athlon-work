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
    ITeamMemberRunExecutor memberExecutor,
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
            LeaderSessionId = "ldr_" + IdGen.NewId()[..10],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
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

        var completed = await RunToCompletionAsync(began.Task.RootSessionId, began.Task.TaskId, cancellationToken)
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
        CancellationToken cancellationToken = default)
    {
        var task = await taskStore.GetAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Team task '{taskId}' not found.");

        var leader = await profileStore.GetAsync(task.LeaderProfileId ?? "", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Leader profile missing.");

        var leaderSessionId = task.LeaderSessionId ?? ("ldr_" + IdGen.NewId()[..10]);
        task.LeaderSessionId = leaderSessionId;
        task.Status = TeamTaskStatus.Running;
        await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);

        var leaderMessage = BuildLeaderKickoff(task);
        try
        {
            using (TeamTaskExecutionScope.Enter(task))
            {
                var outcome = await memberExecutor.ExecuteAsync(
                    task,
                    leader,
                    leaderSessionId,
                    leaderMessage,
                    isLeader: true,
                    cancellationToken).ConfigureAwait(false);

                task = TeamTaskExecutionScope.Current?.Task ?? task;

                if (!outcome.Ok)
                {
                    task.Status = TeamTaskStatus.Failed;
                    task.Error = outcome.Error ?? outcome.ResponseText;
                    task.CompletedAt = DateTimeOffset.UtcNow;
                    await taskStore.SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
                    await traceStore.AppendAsync(
                        task.RootSessionId,
                        task.TaskId,
                        new TeamTraceEvent { Type = "task.failed", Message = task.Error },
                        CancellationToken.None).ConfigureAwait(false);
                    return task;
                }

                // If Leader never submitted, keep Running/AwaitingHuman or auto-complete when all workers done.
                if (task.Status == TeamTaskStatus.Running
                    && task.SubTasks.Count > 0
                    && task.SubTasks.All(s => s.Status is TeamSubTaskStatus.Succeeded or TeamSubTaskStatus.Failed or TeamSubTaskStatus.Cancelled)
                    && string.IsNullOrWhiteSpace(task.FinalResult))
                {
                    task.FinalResult = AggregateResults(task);
                    task.Status = task.SubTasks.Any(s => s.Status == TeamSubTaskStatus.Failed)
                        ? TeamTaskStatus.Failed
                        : TeamTaskStatus.Succeeded;
                    task.CompletedAt = DateTimeOffset.UtcNow;
                }
                else if (task.Status == TeamTaskStatus.Running && string.IsNullOrWhiteSpace(task.FinalResult))
                {
                    // Leader finished without submit — treat assistant text as summary, leave Running only if work pending.
                    if (task.SubTasks.Any(s => s.Status is TeamSubTaskStatus.Ready or TeamSubTaskStatus.Pending or TeamSubTaskStatus.Running))
                    {
                        task.Status = TeamTaskStatus.Running;
                    }
                    else
                    {
                        task.FinalResult = outcome.ResponseText;
                        task.Status = TeamTaskStatus.Succeeded;
                        task.CompletedAt = DateTimeOffset.UtcNow;
                    }
                }
            }

            await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
            return task;
        }
        catch (OperationCanceledException)
        {
            task = await taskStore.GetAsync(rootSessionId, taskId, CancellationToken.None).ConfigureAwait(false) ?? task;
            task.Status = TeamTaskStatus.Cancelled;
            task.Error = "cancelled";
            task.CompletedAt = DateTimeOffset.UtcNow;
            await taskStore.SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
            await traceStore.AppendAsync(
                task.RootSessionId,
                task.TaskId,
                new TeamTraceEvent { Type = "task.cancelled", Message = "cancelled by user" },
                CancellationToken.None).ConfigureAwait(false);
            return task;
        }
    }

    private static string BuildLeaderKickoff(TeamTask task)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Organize the team to complete this goal.");
        sb.AppendLine(task.Goal);
        sb.AppendLine();
        sb.AppendLine("Recommended flow:");
        sb.AppendLine("1) team_list_members");
        sb.AppendLine("2) team_create_subtask for each worker assignment");
        sb.AppendLine("3) team_dispatch");
        sb.AppendLine("4) team_collect");
        sb.AppendLine("5) team_submit_result (or team_redispatch / team_ask_user when needed)");
        return sb.ToString();
    }

    private static string AggregateResults(TeamTask task)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Team result for: {task.Goal}");
        foreach (var sub in task.SubTasks)
        {
            sb.AppendLine();
            sb.AppendLine($"### {sub.Title} ({sub.Status})");
            sb.AppendLine(sub.Result ?? sub.Error ?? "(empty)");
        }

        return sb.ToString();
    }
}
