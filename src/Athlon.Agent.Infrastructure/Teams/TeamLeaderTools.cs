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
}

public sealed class TeamListMembersTool(
    AppSettings settings,
    ITeamDefinitionStore teamStore,
    IAgentProfileStore profileStore) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_list_members",
        "List Team members with roles, AllowRedispatch, MaxIterations, and capability summaries.",
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
            $"- [{role}] {profile.Name} id={profile.Id} redispatch={profile.AllowRedispatch} max_iter={profile.MaxIterations} " +
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
        "Create a subtask for a Worker profile. Optionally set depends_on and handoff text.",
        ToolSchema.Object()
            .String("assignee_profile_id", "Worker profile id", required: true, minLength: 1)
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

        if (!TeamToolHelpers.TryGetTask(out var task, out var gateError))
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

        var team = await teamStore.GetAsync(task.TeamId, cancellationToken).ConfigureAwait(false);
        if (team is null)
        {
            return ToolResult.Failure("Team missing", $"Team '{task.TeamId}' not found.");
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

        var maxIter = profile.AllowRedispatch
            ? Math.Clamp(profile.MaxIterations <= 0 ? settings.Teams.DefaultMaxIterations : profile.MaxIterations, 1, settings.Teams.AbsoluteMaxIterations)
            : 1;

        var sub = new TeamSubTask
        {
            Id = "sub_" + IdGen.NewId()[..8],
            Title = title.Trim(),
            AssigneeProfileId = profile.Id,
            DependsOn = depends,
            Handoff = handoff?.Trim() ?? "",
            Status = depends.Count == 0 ? TeamSubTaskStatus.Ready : TeamSubTaskStatus.Pending,
            Iteration = 1,
            MaxIterations = maxIter
        };

        task.SubTasks.Add(sub);
        TeamTaskExecutionScope.Current?.ReplaceTask(task);
        await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
        await traceStore.AppendAsync(
            task.RootSessionId,
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
            JsonSerializer.Serialize(new { sub.Id, sub.Title, sub.AssigneeProfileId, sub.Status, sub.MaxIterations }));
    }
}

public sealed class TeamDispatchTool(
    AppSettings settings,
    ITeamTaskStore taskStore,
    ITeamTraceStore traceStore) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_dispatch",
        "Enqueue ready subtasks for background execution (respects MaxParallelWorkers in the outer loop). Returns immediately.",
        ToolSchema.Object()
            .String("subtask_ids", "Comma-separated subtask ids; empty = all Ready")
            .Build(),
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

        invocation.Arguments.TryGetString("subtask_ids", out var idsRaw);
        var requested = string.IsNullOrWhiteSpace(idsRaw)
            ? null
            : idsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        TeamWorkerHelpers.RefreshReady(task);
        var candidates = task.SubTasks
            .Where(s => s.Status == TeamSubTaskStatus.Ready)
            .Where(s => requested is null || requested.Contains(s.Id))
            .ToList();

        if (candidates.Count == 0)
        {
            return ToolResult.Success("nothing to dispatch", "No Ready subtasks matched.");
        }

        var results = new StringBuilder();
        foreach (var sub in candidates)
        {
            sub.MemberSessionId ??= "mbr_" + IdGen.NewId()[..10];
            sub.Status = TeamSubTaskStatus.Queued;
            sub.StartedAt = null;
            sub.CompletedAt = null;
            sub.RedispatchReason = null;
            results.AppendLine($"{sub.Id}: queued");
            await traceStore.AppendAsync(
                task.RootSessionId,
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

        TeamTaskExecutionScope.Current?.ReplaceTask(task);
        await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
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
        "Queue another iteration of a completed Worker (loop). Honors AllowRedispatch and MaxIterations. Returns immediately.",
        ToolSchema.Object()
            .String("subtask_id", "Subtask id", required: true, minLength: 1)
            .String("reason", "Why another iteration is needed", required: true, minLength: 1)
            .String("handoff", "Optional updated handoff text")
            .Build(),
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

        invocation.Arguments.TryGetString("subtask_id", out var subId);
        invocation.Arguments.TryGetString("reason", out var reason);
        invocation.Arguments.TryGetString("handoff", out var handoff);
        if (string.IsNullOrWhiteSpace(subId) || string.IsNullOrWhiteSpace(reason))
        {
            return ToolResult.Failure("Invalid args", "subtask_id and reason are required.");
        }

        var sub = task.SubTasks.FirstOrDefault(s => string.Equals(s.Id, subId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (sub is null)
        {
            return ToolResult.Failure("Not found", $"Subtask '{subId}' not found.");
        }

        if (sub.Status is TeamSubTaskStatus.Running or TeamSubTaskStatus.Queued)
        {
            return ToolResult.Failure("Busy", "Subtask is still queued or running.");
        }

        var profile = await profileStore.GetAsync(sub.AssigneeProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return ToolResult.Failure("Unknown profile", "Assignee profile missing.");
        }

        if (!profile.AllowRedispatch)
        {
            return ToolResult.Failure("Redispatch denied", $"Profile '{profile.Name}' has AllowRedispatch=false.");
        }

        if (sub.Iteration >= sub.MaxIterations)
        {
            return ToolResult.Failure(
                "Max iterations",
                $"Subtask reached MaxIterations={sub.MaxIterations}. Summarize or ask the user.");
        }

        sub.Iteration += 1;
        if (!string.IsNullOrWhiteSpace(handoff))
        {
            sub.Handoff = handoff.Trim();
        }

        sub.Status = TeamSubTaskStatus.Queued;
        sub.RedispatchReason = reason.Trim();
        sub.MemberSessionId ??= "mbr_" + IdGen.NewId()[..10];
        sub.CompletedAt = null;

        await traceStore.AppendAsync(
            task.RootSessionId,
            task.TaskId,
            new TeamTraceEvent
            {
                Type = "loop.edge",
                FromNodeId = "leader",
                ToNodeId = sub.Id,
                ProfileId = profile.Id,
                SubTaskId = sub.Id,
                Iteration = sub.Iteration,
                Message = $"loop iter {sub.Iteration}/{sub.MaxIterations}: {reason}"
            },
            cancellationToken).ConfigureAwait(false);

        TeamWorkerHelpers.RefreshReady(task);
        TeamTaskExecutionScope.Current?.ReplaceTask(task);
        await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);

        return ToolResult.Success(
            $"redispatch queued iter {sub.Iteration}",
            $"Subtask {sub.Id} queued for iteration {sub.Iteration}/{sub.MaxIterations}.");
    }
}

public sealed class TeamAwaitTool(AppSettings settings) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_await",
        "Report live status of subtasks (Queued / Running / Ready / Succeeded / Failed).",
        ToolSchema.Object()
            .String("subtask_ids", "Comma-separated ids; empty = all")
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
        var queued = 0;
        var running = 0;
        var ready = 0;
        var succeeded = 0;
        var failed = 0;
        foreach (var sub in task.SubTasks.Where(s => filter is null || filter.Contains(s.Id)))
        {
            sb.AppendLine($"{sub.Id} [{sub.Status}] iter={sub.Iteration}/{sub.MaxIterations} {sub.Title}");
            switch (sub.Status)
            {
                case TeamSubTaskStatus.Queued: queued++; break;
                case TeamSubTaskStatus.Running: running++; break;
                case TeamSubTaskStatus.Ready: ready++; break;
                case TeamSubTaskStatus.Succeeded: succeeded++; break;
                case TeamSubTaskStatus.Failed: failed++; break;
            }
        }

        sb.AppendLine(
            $"summary: queued={queued} running={running} ready={ready} succeeded={succeeded} failed={failed}");
        return Task.FromResult(ToolResult.Success("await status", sb.Length == 0 ? "(no subtasks)" : sb.ToString()));
    }
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

            if (filter is null && sub.Status != TeamSubTaskStatus.Succeeded)
            {
                continue;
            }

            sb.AppendLine($"### {sub.Id} {sub.Title} (iter {sub.Iteration})");
            sb.AppendLine(TeamWorkerHelpers.Truncate(sub.Result ?? sub.Error ?? "(empty)", 1200));
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
        "Submit the final Team Task deliverable and mark the task succeeded.",
        ToolSchema.Object()
            .String("result", "Final deliverable text", required: true, minLength: 1)
            .Build(),
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

        invocation.Arguments.TryGetString("result", out var result);
        if (string.IsNullOrWhiteSpace(result))
        {
            return ToolResult.Failure("Invalid args", "result is required.");
        }

        task.FinalResult = result.Trim();
        task.Status = TeamTaskStatus.Succeeded;
        task.CompletedAt = DateTimeOffset.UtcNow;
        TeamTaskExecutionScope.Current?.ReplaceTask(task);
        await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
        await traceStore.AppendAsync(
            task.RootSessionId,
            task.TaskId,
            new TeamTraceEvent { Type = "task.completed", NodeId = "submit", Message = "submitted" },
            cancellationToken).ConfigureAwait(false);

        return ToolResult.Success("submitted", task.FinalResult);
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

        if (!TeamToolHelpers.TryGetTask(out var task, out var gateError))
        {
            return gateError!;
        }

        invocation.Arguments.TryGetString("question", out var question);
        if (string.IsNullOrWhiteSpace(question))
        {
            return ToolResult.Failure("Invalid args", "question is required.");
        }

        var trimmed = question.Trim();
        task.Status = TeamTaskStatus.AwaitingHuman;
        task.PendingHumanQuestion = trimmed;
        TeamTaskExecutionScope.Current?.ReplaceTask(task);
        await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
        await traceStore.AppendAsync(
            task.RootSessionId,
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
