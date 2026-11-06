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
    IAgentProfileStore profileStore,
    ITeamMemberRunExecutor memberExecutor,
    ITeamTraceStore traceStore) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_dispatch",
        "Start ready subtasks (respects MaxParallelWorkers). Pass subtask_ids or omit to dispatch all Ready.",
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

        RefreshReady(task);
        var candidates = task.SubTasks
            .Where(s => s.Status == TeamSubTaskStatus.Ready)
            .Where(s => requested is null || requested.Contains(s.Id))
            .ToList();

        if (candidates.Count == 0)
        {
            return ToolResult.Success("nothing to dispatch", "No Ready subtasks matched.");
        }

        var teamMax = Math.Max(1, settings.Teams.MaxParallelWorkers);
        var running = task.SubTasks.Count(s => s.Status == TeamSubTaskStatus.Running);
        var results = new StringBuilder();

        // P0: serial dispatch even if parallel cap > 1 (P1 adds true parallel).
        foreach (var sub in candidates)
        {
            if (running >= teamMax)
            {
                results.AppendLine($"{sub.Id}: deferred (parallel cap {teamMax})");
                continue;
            }

            var outcome = await RunWorkerAsync(task, sub, redispatch: false, reason: null, cancellationToken)
                .ConfigureAwait(false);
            results.AppendLine($"{sub.Id}: {(outcome.Ok ? "ok" : "fail")} {Truncate(outcome.ResponseText)}");
            running = task.SubTasks.Count(s => s.Status == TeamSubTaskStatus.Running);
        }

        TeamTaskExecutionScope.Current?.ReplaceTask(task);
        await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
        return ToolResult.Success("dispatch finished", results.ToString());
    }

    private async Task<TeamMemberRunOutcome> RunWorkerAsync(
        TeamTask task,
        TeamSubTask sub,
        bool redispatch,
        string? reason,
        CancellationToken cancellationToken)
    {
        var profile = await profileStore.GetAsync(sub.AssigneeProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            sub.Status = TeamSubTaskStatus.Failed;
            sub.Error = "profile missing";
            return TeamMemberRunOutcome.Fail("profile missing");
        }

        sub.MemberSessionId ??= "mbr_" + IdGen.NewId()[..10];
        sub.Status = TeamSubTaskStatus.Running;
        sub.StartedAt ??= DateTimeOffset.UtcNow;
        if (redispatch)
        {
            sub.RedispatchReason = reason;
        }

        await traceStore.AppendAsync(
            task.RootSessionId,
            task.TaskId,
            new TeamTraceEvent
            {
                Type = redispatch ? "worker.redispatch" : "worker.dispatch",
                NodeId = sub.Id,
                FromNodeId = "leader",
                ToNodeId = sub.Id,
                ProfileId = profile.Id,
                SubTaskId = sub.Id,
                Iteration = sub.Iteration,
                Message = reason
            },
            cancellationToken).ConfigureAwait(false);

        var message = BuildWorkerMessage(task, sub);
        var outcome = await memberExecutor.ExecuteAsync(
            task,
            profile,
            sub.MemberSessionId,
            message,
            isLeader: false,
            cancellationToken).ConfigureAwait(false);

        sub.CompletedAt = DateTimeOffset.UtcNow;
        if (outcome.Ok)
        {
            sub.Status = TeamSubTaskStatus.Succeeded;
            sub.Result = outcome.ResponseText;
            sub.PrevResultSummary = Truncate(outcome.ResponseText, 1200);
            sub.Error = null;
        }
        else
        {
            sub.Status = TeamSubTaskStatus.Failed;
            sub.Error = outcome.Error ?? outcome.ResponseText;
        }

        await traceStore.AppendAsync(
            task.RootSessionId,
            task.TaskId,
            new TeamTraceEvent
            {
                Type = outcome.Ok ? "worker.completed" : "worker.failed",
                NodeId = sub.Id,
                ProfileId = profile.Id,
                SubTaskId = sub.Id,
                Iteration = sub.Iteration,
                Message = Truncate(outcome.ResponseText)
            },
            cancellationToken).ConfigureAwait(false);

        RefreshReady(task);
        return outcome;
    }

    internal static void RefreshReady(TeamTask task)
    {
        var done = task.SubTasks
            .Where(s => s.Status is TeamSubTaskStatus.Succeeded)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sub in task.SubTasks.Where(s => s.Status == TeamSubTaskStatus.Pending))
        {
            if (sub.DependsOn.All(d => done.Contains(d)))
            {
                sub.Status = TeamSubTaskStatus.Ready;
            }
        }
    }

    private static string BuildWorkerMessage(TeamTask task, TeamSubTask sub)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Team goal: {task.Goal}");
        sb.AppendLine($"Subtask: {sub.Title} ({sub.Id})");
        sb.AppendLine($"Iteration: {sub.Iteration}/{sub.MaxIterations}");
        if (!string.IsNullOrWhiteSpace(sub.RedispatchReason))
        {
            sb.AppendLine($"Redispatch reason: {sub.RedispatchReason}");
        }

        if (!string.IsNullOrWhiteSpace(sub.PrevResultSummary) && sub.Iteration > 1)
        {
            sb.AppendLine();
            sb.AppendLine("Previous result summary:");
            sb.AppendLine(sub.PrevResultSummary);
        }

        sb.AppendLine();
        sb.AppendLine("Handoff:");
        sb.AppendLine(string.IsNullOrWhiteSpace(sub.Handoff) ? "(none — use team goal)" : sub.Handoff);
        return sb.ToString();
    }

    private static string Truncate(string? text, int max = 400)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}

public sealed class TeamRedispatchTool(
    AppSettings settings,
    ITeamTaskStore taskStore,
    IAgentProfileStore profileStore,
    ITeamMemberRunExecutor memberExecutor,
    ITeamTraceStore traceStore) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_redispatch",
        "Re-run a completed Worker subtask with a required reason (loop). Honors AllowRedispatch and MaxIterations.",
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

        if (sub.Status is TeamSubTaskStatus.Running)
        {
            return ToolResult.Failure("Busy", "Subtask is still running.");
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

        sub.Status = TeamSubTaskStatus.Running;
        sub.RedispatchReason = reason.Trim();
        var memberSessionId = sub.MemberSessionId ?? ("mbr_" + IdGen.NewId()[..10]);
        sub.MemberSessionId = memberSessionId;

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

        var message = BuildWorkerMessage(task, sub);
        var run = await memberExecutor.ExecuteAsync(task, profile, memberSessionId, message, false, cancellationToken)
            .ConfigureAwait(false);

        sub.CompletedAt = DateTimeOffset.UtcNow;
        if (run.Ok)
        {
            sub.Status = TeamSubTaskStatus.Succeeded;
            sub.Result = run.ResponseText;
            sub.PrevResultSummary = Truncate(run.ResponseText, 1200);
            sub.Error = null;
        }
        else
        {
            sub.Status = TeamSubTaskStatus.Failed;
            sub.Error = run.Error ?? run.ResponseText;
        }

        TeamDispatchTool.RefreshReady(task);
        TeamTaskExecutionScope.Current?.ReplaceTask(task);
        await taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);

        return run.Ok
            ? ToolResult.Success($"redispatch iter {sub.Iteration}", Truncate(run.ResponseText))
            : ToolResult.Failure("redispatch failed", run.Error ?? run.ResponseText);
    }

    private static string BuildWorkerMessage(TeamTask task, TeamSubTask sub)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Team goal: {task.Goal}");
        sb.AppendLine($"Subtask: {sub.Title} ({sub.Id})");
        sb.AppendLine($"Iteration: {sub.Iteration}/{sub.MaxIterations}");
        if (!string.IsNullOrWhiteSpace(sub.RedispatchReason))
        {
            sb.AppendLine($"Redispatch reason: {sub.RedispatchReason}");
        }

        if (!string.IsNullOrWhiteSpace(sub.PrevResultSummary))
        {
            sb.AppendLine();
            sb.AppendLine("Previous result summary:");
            sb.AppendLine(sub.PrevResultSummary);
        }

        sb.AppendLine();
        sb.AppendLine("Handoff:");
        sb.AppendLine(string.IsNullOrWhiteSpace(sub.Handoff) ? "(none)" : sub.Handoff);
        return sb.ToString();
    }

    private static string Truncate(string? text, int max = 400)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}

public sealed class TeamAwaitTool(AppSettings settings) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_await",
        "Report status of subtasks (P0 is synchronous — use after dispatch).",
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
        foreach (var sub in task.SubTasks.Where(s => filter is null || filter.Contains(s.Id)))
        {
            sb.AppendLine($"{sub.Id} [{sub.Status}] iter={sub.Iteration}/{sub.MaxIterations} {sub.Title}");
        }

        return Task.FromResult(ToolResult.Success("await status", sb.Length == 0 ? "(no subtasks)" : sb.ToString()));
    }
}

public sealed class TeamCollectTool(AppSettings settings) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_collect",
        "Collect Result / artifacts from subtasks.",
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
            sb.AppendLine(sub.Result ?? sub.Error ?? "(empty)");
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

public sealed class TeamAskUserTool(AppSettings settings) : IAgentTool, ITeamLeaderTool, IExcludedFromChildAgentToolkit
{
    public ToolDefinition Definition => new(
        "team_ask_user",
        "Pause the team task and ask the human a clarifying question.",
        ToolSchema.Object()
            .String("question", "Question for the user", required: true, minLength: 1)
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

        invocation.Arguments.TryGetString("question", out var question);
        if (string.IsNullOrWhiteSpace(question))
        {
            return Task.FromResult(ToolResult.Failure("Invalid args", "question is required."));
        }

        task.Status = TeamTaskStatus.AwaitingHuman;
        TeamTaskExecutionScope.Current?.ReplaceTask(task);
        return Task.FromResult(ToolResult.Success(
            "awaiting human",
            $"ATHLON_TEAM_ASK_USER\n{question.Trim()}"));
    }
}
