using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

internal static class TeamToolHelpers
{
    public static bool TryGetTask(out TeamTask task, out ToolResult? error)
    {
        task = TeamTaskExecutionScope.Current?.Task!;
        if (task is null)
        {
            error = ToolResult.Failure("No team task", "team_* tools require an active TeamTaskExecutionScope.");
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Reloads task.json under the per-task lock, applies <paramref name="mutate"/>, saves, and
    /// refreshes <see cref="TeamTaskExecutionScope"/>.
    /// </summary>
    public static async Task<(TeamTask? Task, ToolResult? Error)> MutateLockedAsync(
        ITeamTaskStore taskStore,
        Func<TeamTask, ToolResult?> mutate,
        CancellationToken cancellationToken)
    {
        if (!TryGetTask(out var scoped, out var gateError))
        {
            return (null, gateError);
        }

        using (await TeamTaskLock.AcquireAsync(scoped.RootSessionId, scoped.TaskId, cancellationToken)
                   .ConfigureAwait(false))
        {
            var task = await taskStore.GetAsync(scoped.RootSessionId, scoped.TaskId, cancellationToken)
                .ConfigureAwait(false);
            if (task is null)
            {
                return (null, ToolResult.Failure("Missing task", $"Team task '{scoped.TaskId}' not found."));
            }

            var error = mutate(task);
            if (error is not null)
            {
                return (task, error);
            }

            TeamTaskExecutionScope.Current?.ReplaceTask(task);
            await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
            return (task, null);
        }
    }

    public static bool WouldCreateDependencyCycle(TeamTask task, string newSubId, IReadOnlyList<string> dependsOn)
    {
        if (dependsOn.Count == 0)
        {
            return false;
        }

        if (dependsOn.Any(d => string.Equals(d, newSubId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var edges = task.SubTasks.ToDictionary(
            s => s.Id,
            s => (IReadOnlyList<string>)s.DependsOn,
            StringComparer.OrdinalIgnoreCase);
        edges[newSubId] = dependsOn;

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Dfs(string node)
        {
            if (!visiting.Add(node))
            {
                return true;
            }

            if (visited.Contains(node))
            {
                visiting.Remove(node);
                return false;
            }

            if (edges.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (Dfs(dep))
                    {
                        return true;
                    }
                }
            }

            visiting.Remove(node);
            visited.Add(node);
            return false;
        }

        return Dfs(newSubId);
    }
}

public sealed class TeamListMembersTool(
    AppSettings settings,
    ITeamDefinitionStore teamStore,
    IAgentProfileStore profileStore) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_list_members",
        "List the Worker pool with roles and capability summaries. Use this to pick who to schedule — do not start every Worker.",
        ToolSchema.Object().Build(),
        Group: ToolGroup.Builtin);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return ToolResult.Failure("Teams disabled", "Teams feature is disabled.");
        }

        if (!TeamToolHelpers.TryGetTask(out var task, out var gateError))
        {
            return gateError!;
        }

        var team = await teamStore.GetAsync(task.TeamId, cancellationToken).ConfigureAwait(false);
        if (team is null)
        {
            return ToolResult.Failure("Team missing", $"Team '{task.TeamId}' not found.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"team: {team.Name} ({team.Id})");
        sb.AppendLine($"max_parallel: {team.MaxParallelWorkers}");
        sb.AppendLine(
            "note: workers below are a pool. Schedule only those needed for the current goal/wave; idle workers stay unused.");
        await AppendProfileAsync(sb, team.LeaderProfileId, "Leader", cancellationToken).ConfigureAwait(false);
        foreach (var workerId in team.WorkerProfileIds)
        {
            await AppendProfileAsync(sb, workerId, "Worker", cancellationToken).ConfigureAwait(false);
        }

        return ToolResult.Success("members listed", sb.ToString());
    }

    private async Task AppendProfileAsync(StringBuilder sb, string profileId, string role, CancellationToken ct)
    {
        var profile = await profileStore.GetAsync(profileId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            sb.AppendLine($"- [{role}] missing profile {profileId}");
            return;
        }

        sb.AppendLine(
            $"- [{role}] {profile.Name} id={profile.Id} " +
            $"skills={profile.SkillNames.Count} mcp={profile.McpServerNames.Count} kb={profile.KnowledgeModuleIds.Count}");
        if (!string.IsNullOrWhiteSpace(profile.Description))
        {
            sb.AppendLine($"  {profile.Description.Trim()}");
        }
    }
}

public sealed class TeamCreateSubtaskTool(
    AppSettings settings,
    ITeamTaskStore taskStore,
    ITeamDefinitionStore teamStore,
    IAgentProfileStore profileStore,
    ITeamTraceStore traceStore) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_create_subtask",
        "Create a subtask for one Worker you chose to schedule now (not every team member). Optionally set depends_on and handoff.",
        ToolSchema.Object()
            .String("assignee_profile_id", "Worker profile id to schedule", required: true, minLength: 1)
            .String("title", "Short title", required: true, minLength: 1)
            .String("handoff", "Handoff payload (goal / facts / todos / refs)")
            .String("depends_on", "Comma-separated subtask ids this depends on")
            .Build(),
        Group: ToolGroup.Builtin);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return ToolResult.Failure("Teams disabled", "Teams feature is disabled.");
        }

        if (!TeamToolHelpers.TryGetTask(out var scoped, out var gateError))
        {
            return gateError!;
        }

        invocation.Arguments.TryGetString("assignee_profile_id", out var assignee);
        invocation.Arguments.TryGetString("title", out var title);
        invocation.Arguments.TryGetString("handoff", out var handoff);
        invocation.Arguments.TryGetString("depends_on", out var dependsRaw);

        if (string.IsNullOrWhiteSpace(assignee) || string.IsNullOrWhiteSpace(title))
        {
            return ToolResult.Failure("Invalid args", "assignee_profile_id and title are required.");
        }

        var profile = await profileStore.GetAsync(assignee.Trim(), cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return ToolResult.Failure("Unknown profile", $"Profile '{assignee}' not found.");
        }

        var team = await teamStore.GetAsync(scoped.TeamId, cancellationToken).ConfigureAwait(false);
        if (team is null)
        {
            return ToolResult.Failure("Team missing", $"Team '{scoped.TeamId}' not found.");
        }

        if (!team.WorkerProfileIds.Contains(profile.Id, StringComparer.OrdinalIgnoreCase))
        {
            return ToolResult.Failure(
                "Not a worker",
                $"Profile '{profile.Name}' is not a Worker on team '{team.Name}'.");
        }

        var depends = string.IsNullOrWhiteSpace(dependsRaw)
            ? new List<string>()
            : dependsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        TeamSubTask? created = null;
        var (task, error) = await TeamToolHelpers.MutateLockedAsync(
            taskStore,
            live =>
            {
                if (depends.Count > 0)
                {
                    var known = new HashSet<string>(
                        live.SubTasks.Select(s => s.Id),
                        StringComparer.OrdinalIgnoreCase);
                    var missing = depends.Where(d => !known.Contains(d))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (missing.Count > 0)
                    {
                        return ToolResult.Failure(
                            "Invalid depends_on",
                            $"Unknown dependency id(s): {string.Join(", ", missing)}. "
                            + "depends_on must reference existing subtask ids on this task.");
                    }
                }

                var newId = "sub_" + IdGen.NewId()[..8];
                if (TeamToolHelpers.WouldCreateDependencyCycle(live, newId, depends))
                {
                    return ToolResult.Failure(
                        "Dependency cycle",
                        "depends_on would create a cycle (including self-dependency).");
                }

                // Next-wave creates share MaxRepairRounds with redispatch.
                var isNextWave = live.SubTasks.Any(s =>
                    s.Status is TeamSubTaskStatus.Succeeded or TeamSubTaskStatus.Failed);
                var maxRepair = Math.Max(1, settings.Teams.MaxRepairRounds);
                if (isNextWave && live.RepairRoundsUsed >= maxRepair)
                {
                    return ToolResult.Failure(
                        "Max scheduling rounds",
                        $"Team reached MaxRepairRounds={maxRepair}. Ask the user or submit partial/failed.");
                }

#pragma warning disable CS0618
                var sub = new TeamSubTask
                {
                    Id = newId,
                    Title = title.Trim(),
                    AssigneeProfileId = profile.Id,
                    DependsOn = depends,
                    Handoff = handoff?.Trim() ?? "",
                    Status = depends.Count == 0 ? TeamSubTaskStatus.Ready : TeamSubTaskStatus.Pending,
                    Iteration = 1,
                    MaxIterations = maxRepair
                };
#pragma warning restore CS0618

                live.SubTasks.Add(sub);
                if (isNextWave)
                {
                    live.RepairRoundsUsed += 1;
                }

                created = sub;
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        var sub = created!;
        await traceStore.AppendAsync(
            task!.RootSessionId,
            task.TaskId,
            new TeamTraceEvent
            {
                Type = "subtask.created",
                NodeId = sub.Id,
                ProfileId = profile.Id,
                SubTaskId = sub.Id,
                Message = sub.Title
            },
            cancellationToken).ConfigureAwait(false);

        return ToolResult.Success(
            "subtask created",
            JsonSerializer.Serialize(new { sub.Id, sub.Title, sub.AssigneeProfileId, sub.Status, attempt = sub.Iteration }));
    }
}

public sealed class TeamDispatchTool(
    AppSettings settings,
    ITeamTaskStore taskStore,
    ITeamTraceStore traceStore) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_dispatch",
        "Enqueue the Ready subtasks you created for this wave (do not invent work for idle Workers). Returns immediately; outer loop runs them.",
        ToolSchema.Object()
            .String("subtask_ids", "Comma-separated subtask ids; empty = all Ready in this wave")
            .Build(),
        Group: ToolGroup.Builtin);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return ToolResult.Failure("Teams disabled", "Teams feature is disabled.");
        }

        invocation.Arguments.TryGetString("subtask_ids", out var idsRaw);
        var requested = string.IsNullOrWhiteSpace(idsRaw)
            ? null
            : idsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var queued = new List<TeamSubTask>();
        var nothingToDispatch = false;
        var (task, error) = await TeamToolHelpers.MutateLockedAsync(
            taskStore,
            live =>
            {
                TeamWorkerHelpers.RefreshReady(live);
                var candidates = live.SubTasks
                    .Where(s => s.Status == TeamSubTaskStatus.Ready)
                    .Where(s => requested is null || requested.Contains(s.Id))
                    .ToList();
                if (candidates.Count == 0)
                {
                    nothingToDispatch = true;
                    return ToolResult.Failure("noop", "No Ready subtasks matched.");
                }

                foreach (var sub in candidates)
                {
                    sub.MemberSessionId ??= "mbr_" + IdGen.NewId()[..10];
                    sub.Status = TeamSubTaskStatus.Queued;
                    sub.StartedAt = null;
                    sub.CompletedAt = null;
                    sub.RedispatchReason = null;
                    queued.Add(sub);
                }

                return null;
            },
            cancellationToken).ConfigureAwait(false);

        if (nothingToDispatch)
        {
            return ToolResult.Success("nothing to dispatch", "No Ready subtasks matched.");
        }

        if (error is not null)
        {
            return error;
        }

        var results = new StringBuilder();
        foreach (var sub in queued)
        {
            results.AppendLine($"{sub.Id}: queued");
            await traceStore.AppendAsync(
                task!.RootSessionId,
                task.TaskId,
                new TeamTraceEvent
                {
                    Type = "worker.queued",
                    NodeId = sub.Id,
                    FromNodeId = "leader",
                    ToNodeId = sub.Id,
                    ProfileId = sub.AssigneeProfileId,
                    SubTaskId = sub.Id,
                    Iteration = sub.Iteration,
                    Message = "queued for outer loop"
                },
                cancellationToken).ConfigureAwait(false);
        }

        return ToolResult.Success("dispatch enqueued", results.ToString());
    }
}

public sealed class TeamRedispatchTool(
    AppSettings settings,
    ITeamTaskStore taskStore,
    IAgentProfileStore profileStore,
    ITeamTraceStore traceStore) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_redispatch",
        "Queue another attempt of a finished Worker (Failed or repair of Succeeded). "
        + "Succeeded repairs require confirm_repair=true. Gated by MaxRepairRounds.",
        ToolSchema.Object()
            .String("subtask_id", "Subtask id", required: true, minLength: 1)
            .String("reason", "Why another attempt is needed", required: true, minLength: 1)
            .String("handoff", "Updated handoff with verification findings / repair instructions", required: true, minLength: 1)
            .Boolean("confirm_repair", "Required true when redistributing a Succeeded subtask")
            .Build(),
        Group: ToolGroup.Builtin);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return ToolResult.Failure("Teams disabled", "Teams feature is disabled.");
        }

        invocation.Arguments.TryGetString("subtask_id", out var subId);
        invocation.Arguments.TryGetString("reason", out var reason);
        invocation.Arguments.TryGetString("handoff", out var handoff);
        var confirmRepair = invocation.Arguments.GetBoolean("confirm_repair", false);
        if (string.IsNullOrWhiteSpace(subId) || string.IsNullOrWhiteSpace(reason))
        {
            return ToolResult.Failure("Invalid args", "subtask_id and reason are required.");
        }

        if (string.IsNullOrWhiteSpace(handoff))
        {
            return ToolResult.Failure(
                "Invalid args",
                "handoff is required on redispatch so the worker receives verification findings / repair instructions.");
        }

        string? profileId = null;
        int attempt = 0;
        int repairUsed = 0;
        int maxRepair = Math.Max(1, settings.Teams.MaxRepairRounds);
        string? queuedId = null;

        var (task, error) = await TeamToolHelpers.MutateLockedAsync(
            taskStore,
            live =>
            {
                var sub = live.SubTasks.FirstOrDefault(s =>
                    string.Equals(s.Id, subId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (sub is null)
                {
                    return ToolResult.Failure("Not found", $"Subtask '{subId}' not found.");
                }

                if (sub.Status is TeamSubTaskStatus.Running or TeamSubTaskStatus.Queued)
                {
                    return ToolResult.Failure("Busy", "Subtask is still queued or running.");
                }

                if (sub.Status == TeamSubTaskStatus.Succeeded && !confirmRepair)
                {
                    return ToolResult.Failure(
                        "Redispatch denied",
                        "Succeeded subtasks require confirm_repair=true (verification failure / repair).");
                }

                if (live.RepairRoundsUsed >= maxRepair)
                {
                    return ToolResult.Failure(
                        "Max repair rounds",
                        $"Team reached MaxRepairRounds={maxRepair}. Collect findings, ask the user, or submit disposition=partial|failed.");
                }

                sub.Iteration += 1;
                sub.Handoff = handoff.Trim();
                sub.Status = TeamSubTaskStatus.Queued;
                sub.RedispatchReason = reason.Trim();
                sub.MemberSessionId = "mbr_" + IdGen.NewId()[..10];
                sub.CompletedAt = null;
                sub.Result = null;
                sub.Error = null;
                live.RepairRoundsUsed += 1;
                TeamWorkerHelpers.RefreshReady(live);

                profileId = sub.AssigneeProfileId;
                attempt = sub.Iteration;
                repairUsed = live.RepairRoundsUsed;
                queuedId = sub.Id;
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        var profile = await profileStore.GetAsync(profileId ?? "", cancellationToken).ConfigureAwait(false);
        await traceStore.AppendAsync(
            task!.RootSessionId,
            task.TaskId,
            new TeamTraceEvent
            {
                Type = "loop.edge",
                FromNodeId = "leader",
                ToNodeId = queuedId,
                ProfileId = profile?.Id ?? profileId,
                SubTaskId = queuedId,
                Iteration = attempt,
                Message = $"loop attempt {attempt} (repair {repairUsed}/{maxRepair}): {reason}"
            },
            cancellationToken).ConfigureAwait(false);

        return ToolResult.Success(
            $"redispatch queued attempt {attempt}",
            $"Subtask {queuedId} queued for attempt {attempt} (repair {repairUsed}/{maxRepair}).");
    }
}

/// <summary>
/// Deprecated alias of <see cref="TeamProgressTool"/>. Prefer team_progress.
/// Kept for older prompts; not leader-gated so it cannot be used as an in-turn poll loop substitute.
/// </summary>
public sealed class TeamAwaitTool(
    AppSettings settings,
    ITeamTaskStore taskStore) : IAgentTool
{
    private readonly TeamProgressTool _inner = new(settings, taskStore);

    public ToolDefinition Definition => new(
        "team_await",
        "Deprecated alias of team_progress. Do not poll after team_dispatch — end your turn. Prefer team_progress.",
        ToolSchema.Object()
            .String("subtask_ids", "Comma-separated ids; empty = all")
            .Build(),
        Group: ToolGroup.Builtin);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        _inner.InvokeAsync(invocation, cancellationToken);
}

public sealed class TeamCollectTool(AppSettings settings) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_collect",
        "Collect Result / artifacts from subtasks (each result truncated).",
        ToolSchema.Object()
            .String("subtask_ids", "Comma-separated ids; empty = all succeeded")
            .Build(),
        Group: ToolGroup.Builtin);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return Task.FromResult(ToolResult.Failure("Teams disabled", "Teams feature is disabled."));
        }

        if (!TeamToolHelpers.TryGetTask(out var task, out var gateError))
        {
            return Task.FromResult(gateError!);
        }

        invocation.Arguments.TryGetString("subtask_ids", out var idsRaw);
        var filter = string.IsNullOrWhiteSpace(idsRaw)
            ? null
            : idsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        foreach (var sub in task.SubTasks)
        {
            if (filter is not null && !filter.Contains(sub.Id))
            {
                continue;
            }

            if (filter is null
                && sub.Status is not (TeamSubTaskStatus.Succeeded or TeamSubTaskStatus.Failed))
            {
                continue;
            }

            sb.AppendLine($"### {sub.Id} [{sub.Status}] {sub.Title} (iter {sub.Iteration})");
            // Keep enough room for verification findings; Trace uses shorter truncation separately.
            sb.AppendLine(TeamWorkerHelpers.Truncate(sub.Result ?? sub.Error ?? "(empty)", 8000));
            sb.AppendLine();
        }

        return Task.FromResult(ToolResult.Success("collected", sb.Length == 0 ? "(nothing)" : sb.ToString()));
    }
}

public sealed class TeamSubmitResultTool(
    AppSettings settings,
    ITeamTaskStore taskStore,
    ITeamTraceStore traceStore) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_submit_result",
        "Submit the final Team Task deliverable. disposition must be completed|partial|failed.",
        ToolSchema.Object()
            .String("result", "Final deliverable text", required: true, minLength: 1)
            .String("disposition", "completed | partial | failed", required: true, minLength: 1)
            .Build(),
        Group: ToolGroup.Builtin);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return ToolResult.Failure("Teams disabled", "Teams feature is disabled.");
        }

        invocation.Arguments.TryGetString("result", out var result);
        invocation.Arguments.TryGetString("disposition", out var dispositionRaw);
        if (string.IsNullOrWhiteSpace(result))
        {
            return ToolResult.Failure("Invalid args", "result is required.");
        }

        var disposition = (dispositionRaw ?? "").Trim().ToLowerInvariant();
        if (disposition is not ("completed" or "partial" or "failed"))
        {
            return ToolResult.Failure(
                "Invalid args",
                "disposition is required and must be completed, partial, or failed.");
        }

        var (task, error) = await TeamToolHelpers.MutateLockedAsync(
            taskStore,
            live =>
            {
                var active = live.SubTasks.Where(s =>
                    s.Status is TeamSubTaskStatus.Pending
                        or TeamSubTaskStatus.Ready
                        or TeamSubTaskStatus.Queued
                        or TeamSubTaskStatus.Running).ToList();
                if (active.Count > 0)
                {
                    return ToolResult.Failure(
                        "Work remaining",
                        "Cannot submit while subtasks are still Pending/Ready/Queued/Running: "
                        + string.Join(", ", active.Select(s => s.Id)));
                }

                var failed = live.SubTasks.Where(s => s.Status == TeamSubTaskStatus.Failed).ToList();
                if (failed.Count > 0 && disposition == "completed")
                {
                    return ToolResult.Failure(
                        "Unresolved failures",
                        "One or more subtasks Failed. Use disposition=partial or disposition=failed, or redispatch. Failed: "
                        + string.Join(", ", failed.Select(s => s.Id)));
                }

                live.FinalResult = result.Trim();
                live.Status = disposition == "failed" ? TeamTaskStatus.Failed : TeamTaskStatus.Succeeded;
                if (disposition == "failed")
                {
                    live.Error = "submitted with disposition=failed";
                }

                live.CompletedAt = DateTimeOffset.UtcNow;
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        await traceStore.AppendAsync(
            task!.RootSessionId,
            task.TaskId,
            new TeamTraceEvent
            {
                Type = disposition == "failed" ? "task.failed" : "task.completed",
                NodeId = "submit",
                Message = $"submitted disposition={disposition}"
            },
            cancellationToken).ConfigureAwait(false);

        return ToolResult.Success("submitted", task.FinalResult ?? result.Trim());
    }
}

public sealed class TeamAskUserTool(
    AppSettings settings,
    ITeamTaskStore taskStore,
    ITeamTraceStore traceStore) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_ask_user",
        "Pause the team task and ask the human a clarifying question. Outer loop exits until the user answers.",
        ToolSchema.Object()
            .String("question", "Question for the user", required: true, minLength: 1)
            .Build(),
        Group: ToolGroup.Builtin);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return ToolResult.Failure("Teams disabled", "Teams feature is disabled.");
        }

        invocation.Arguments.TryGetString("question", out var question);
        if (string.IsNullOrWhiteSpace(question))
        {
            return ToolResult.Failure("Invalid args", "question is required.");
        }

        var trimmed = question.Trim();
        var (task, error) = await TeamToolHelpers.MutateLockedAsync(
            taskStore,
            live =>
            {
                live.Status = TeamTaskStatus.AwaitingHuman;
                live.PendingHumanQuestion = trimmed;
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        await traceStore.AppendAsync(
            task!.RootSessionId,
            task.TaskId,
            new TeamTraceEvent
            {
                Type = "task.awaiting_human",
                NodeId = "leader",
                Message = TeamWorkerHelpers.Truncate(trimmed, 400)
            },
            cancellationToken).ConfigureAwait(false);

        return ToolResult.Success(
            "awaiting human",
            $"ATHLON_TEAM_ASK_USER\n{trimmed}");
    }
}
