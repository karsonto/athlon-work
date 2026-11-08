using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

/// <summary>
/// Resolves the active team task for shared (non-leader) tools from ambient scope or profile turn.
/// </summary>
internal static class TeamSharedToolContext
{
    public static async Task<(TeamTask? Task, ToolResult? Error)> TryResolveTaskAsync(
        ITeamTaskStore taskStore,
        CancellationToken cancellationToken)
    {
        if (TeamTaskExecutionScope.Current?.Task is { } scoped)
        {
            return (scoped, null);
        }

        var options = AgentProfileTurnScope.Current?.Options;
        if (options is null
            || string.IsNullOrWhiteSpace(options.RootSessionId)
            || string.IsNullOrWhiteSpace(options.TeamTaskId))
        {
            return (null, ToolResult.Failure(
                "No team task",
                "team_progress / team_memory require an active Team member turn."));
        }

        var task = await taskStore
            .GetAsync(options.RootSessionId, options.TeamTaskId, cancellationToken)
            .ConfigureAwait(false);
        if (task is null)
        {
            return (null, ToolResult.Failure(
                "Task missing",
                $"Team task '{options.TeamTaskId}' was not found."));
        }

        return (task, null);
    }
}

/// <summary>Read-only task progress for Leader and Workers (not leader-gated).</summary>
public sealed class TeamProgressTool(
    AppSettings settings,
    ITeamTaskStore taskStore) : IAgentTool
{
    public ToolDefinition Definition => new(
        "team_progress",
        "Read current Team Task progress (subtask statuses, dependencies, short result summaries). "
        + "Do not poll in a loop after team_dispatch — end your turn; the outer loop re-invokes the Leader when workers finish. "
        + "Workers may call this to see sibling progress without reading other members' transcripts.",
        ToolSchema.Object()
            .String("subtask_ids", "Comma-separated ids; empty = all")
            .Build(),
        Group: ToolGroup.Builtin);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return ToolResult.Failure("Teams disabled", "Teams feature is disabled.");
        }

        var (task, error) = await TeamSharedToolContext.TryResolveTaskAsync(taskStore, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        invocation.Arguments.TryGetString("subtask_ids", out var idsRaw);
        var filter = string.IsNullOrWhiteSpace(idsRaw)
            ? null
            : idsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine($"task: {task!.TaskId} status={task.Status}");
        sb.AppendLine($"goal: {TeamWorkerHelpers.Truncate(task.Goal, 400)}");
        if (task.Status == TeamTaskStatus.AwaitingHuman
            && !string.IsNullOrWhiteSpace(task.PendingHumanQuestion))
        {
            sb.AppendLine($"awaiting_human: {TeamWorkerHelpers.Truncate(task.PendingHumanQuestion, 400)}");
        }

        var queued = 0;
        var running = 0;
        var ready = 0;
        var succeeded = 0;
        var failed = 0;
        var cancelled = 0;
        foreach (var sub in task.SubTasks.Where(s => filter is null || filter.Contains(s.Id)))
        {
            sb.AppendLine(
                $"{sub.Id} [{sub.Status}] attempt={sub.Iteration} assignee={sub.AssigneeProfileId} {sub.Title}");
            if (sub.DependsOn.Count > 0)
            {
                sb.AppendLine($"  depends_on: {string.Join(", ", sub.DependsOn)}");
            }

            if (!string.IsNullOrWhiteSpace(sub.Result) || !string.IsNullOrWhiteSpace(sub.Error))
            {
                sb.AppendLine(
                    $"  summary: {TeamWorkerHelpers.Truncate(sub.Result ?? sub.Error, 600)}");
            }

            switch (sub.Status)
            {
                case TeamSubTaskStatus.Queued: queued++; break;
                case TeamSubTaskStatus.Running: running++; break;
                case TeamSubTaskStatus.Ready: ready++; break;
                case TeamSubTaskStatus.Succeeded: succeeded++; break;
                case TeamSubTaskStatus.Failed: failed++; break;
                case TeamSubTaskStatus.Cancelled: cancelled++; break;
            }
        }

        sb.AppendLine(
            $"summary: queued={queued} running={running} ready={ready} succeeded={succeeded} failed={failed} cancelled={cancelled}");
        return ToolResult.Success(
            "progress",
            task.SubTasks.Count == 0 ? "task has no subtasks yet" : sb.ToString());
    }
}

/// <summary>Shared team memory blackboard for Leader and Workers.</summary>
public sealed class TeamMemoryTool(
    AppSettings settings,
    ITeamTaskStore taskStore,
    ITeamMemoryStore memoryStore) : IAgentTool
{
    public ToolDefinition Definition => new(
        "team_memory",
        "Read/write global Team Task memory (shared blackboard). "
        + "Use for short conclusions, artifact paths, verification findings — not full transcripts. "
        + "Actions: list | get | put | delete.",
        ToolSchema.Object()
            .String("action", "list | get | put | delete", required: true, minLength: 1)
            .String("key", "Memory key (required for get/put/delete)")
            .String("content", "Content for put (truncated if long)")
            .String("tags", "Comma-separated tags for put")
            .Integer("limit", "Max entries for list (default 40)", defaultValue: 40, minimum: 1)
            .Build(),
        Group: ToolGroup.Builtin);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return ToolResult.Failure("Teams disabled", "Teams feature is disabled.");
        }

        var (task, error) = await TeamSharedToolContext.TryResolveTaskAsync(taskStore, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        invocation.Arguments.TryGetString("action", out var actionRaw);
        var action = (actionRaw ?? "").Trim().ToLowerInvariant();
        invocation.Arguments.TryGetString("key", out var key);
        invocation.Arguments.TryGetString("content", out var content);
        invocation.Arguments.TryGetString("tags", out var tagsRaw);
        var limit = ToolArguments.GetInt32(invocation, "limit", 40);
        limit = Math.Clamp(limit, 1, ITeamMemoryStore.MaxEntries);

        var author = AgentProfileTurnScope.Current?.Options.ProfileId;

        switch (action)
        {
            case "list":
            {
                var entries = await memoryStore
                    .ListAsync(task!.RootSessionId, task.TaskId, limit, cancellationToken)
                    .ConfigureAwait(false);
                if (entries.Count == 0)
                {
                    return ToolResult.Success("empty", "(no memory entries)");
                }

                var sb = new StringBuilder();
                foreach (var entry in entries)
                {
                    sb.AppendLine(
                        $"- {entry.Key} @ {entry.At:o} author={entry.AuthorProfileId ?? "-"} tags=[{string.Join(",", entry.Tags)}]");
                    sb.AppendLine($"  {TeamWorkerHelpers.Truncate(entry.Content, 500)}");
                }

                return ToolResult.Success("listed", sb.ToString());
            }
            case "get":
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return ToolResult.Failure("Invalid args", "key is required for get.");
                }

                var entry = await memoryStore
                    .GetAsync(task!.RootSessionId, task.TaskId, key, cancellationToken)
                    .ConfigureAwait(false);
                if (entry is null)
                {
                    return ToolResult.Failure("Not found", $"No memory entry '{key}'.");
                }

                return ToolResult.Success(
                    "got",
                    $"key={entry.Key}\nauthor={entry.AuthorProfileId}\nat={entry.At:o}\ntags={string.Join(",", entry.Tags)}\n\n{entry.Content}");
            }
            case "put":
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return ToolResult.Failure("Invalid args", "key is required for put.");
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    return ToolResult.Failure("Invalid args", "content is required for put.");
                }

                var tags = string.IsNullOrWhiteSpace(tagsRaw)
                    ? new List<string>()
                    : tagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                await memoryStore.PutAsync(
                    task!.RootSessionId,
                    task.TaskId,
                    new TeamMemoryEntry
                    {
                        Key = key.Trim(),
                        Content = content,
                        Tags = tags,
                        AuthorProfileId = author
                    },
                    cancellationToken).ConfigureAwait(false);
                return ToolResult.Success("stored", $"Stored memory key '{key.Trim()}'.");
            }
            case "delete":
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return ToolResult.Failure("Invalid args", "key is required for delete.");
                }

                var removed = await memoryStore
                    .DeleteAsync(task!.RootSessionId, task.TaskId, key, cancellationToken)
                    .ConfigureAwait(false);
                return removed
                    ? ToolResult.Success("deleted", $"Deleted memory key '{key.Trim()}'.")
                    : ToolResult.Failure("Not found", $"No memory entry '{key}'.");
            }
            default:
                return ToolResult.Failure("Invalid args", "action must be list | get | put | delete.");
        }
    }
}
