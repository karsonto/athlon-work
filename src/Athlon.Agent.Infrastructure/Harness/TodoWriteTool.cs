using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class TodoWriteTool(
    ISessionTaskListStore taskListStore,
    IActiveAgentSessionContext activeSessionContext,
    ITaskListChangedNotifier taskListChangedNotifier,
    IAppLogger logger) : IAgentTool, IHarnessTool, IExcludedFromChildAgentToolkit
{
    private readonly IAppLogger _logger = logger.ForContext("TodoWriteTool");

    public ToolDefinition Definition => new(
        Name: "todo_write",
        Description:
            "Create or update the session task list for multi-step work. "
            + "Pass todos as a JSON array of objects with id, content, and status "
            + "(pending, in_progress, completed, cancelled). "
            + "Set merge=true to update existing items by id; merge=false to replace the full list. "
            + "Do not use for trivial single-step requests or pure Q&A.",
        ParametersSchema: ToolSchema.Object()
            .String("todos", "JSON array string, e.g. [{\"id\":\"1\",\"content\":\"...\",\"status\":\"pending\"}]", required: true)
            .Boolean("merge", "Merge by id when true; replace entire list when false", defaultValue: true)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var sessionId = activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ToolResult.Failure("No session", "todo_write requires an active agent session.");
        }

        if (!invocation.Arguments.TryGetValue("todos", out var todosJson) || string.IsNullOrWhiteSpace(todosJson))
        {
            return ToolResult.Failure("Missing todos", "Required parameter: todos (JSON array)");
        }

        var merge = true;
        if (invocation.Arguments.TryGetValue("merge", out var mergeText)
            && !string.IsNullOrWhiteSpace(mergeText)
            && !bool.TryParse(mergeText.Trim(), out merge))
        {
            return ToolResult.Failure("Invalid merge", "merge must be true or false");
        }

        List<AgentTaskItem> parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<AgentTaskItem>>(todosJson, JsonFileStore.Options)
                ?? [];
        }
        catch (JsonException ex)
        {
            return ToolResult.Failure("Invalid todos JSON", ex.Message);
        }

        if (parsed.Count == 0)
        {
            return ToolResult.Failure("Empty todos", "todos must contain at least one item");
        }

        var normalized = new List<AgentTaskItem>();
        foreach (var item in parsed)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                return ToolResult.Failure("Invalid todo", "Each todo must have a non-empty id");
            }

            if (string.IsNullOrWhiteSpace(item.Content))
            {
                return ToolResult.Failure("Invalid todo", $"Todo '{item.Id}' must have non-empty content");
            }

            var status = AgentTaskStatuses.Normalize(item.Status);
            if (!AgentTaskStatuses.All.Contains(status))
            {
                return ToolResult.Failure(
                    "Invalid status",
                    $"Todo '{item.Id}' has invalid status '{item.Status}'. Use pending, in_progress, completed, or cancelled.");
            }

            normalized.Add(new AgentTaskItem
            {
                Id = item.Id.Trim(),
                Content = item.Content.Trim(),
                Status = status
            });
        }

        try
        {
            var list = await taskListStore.ApplyMergeAsync(sessionId, normalized, merge, cancellationToken).ConfigureAwait(false);
            taskListChangedNotifier.Notify(sessionId);
            var summary = BuildSummary(list);
            _logger.Information("Updated task list for session {SessionId}: {Summary}", sessionId, summary);
            return ToolResult.Success(summary, FormatListForAgent(list));
        }
        catch (Exception ex)
        {
            _logger.Warning("todo_write failed: {Error}", ex.Message);
            return ToolResult.Failure("Persist failed", ex.Message);
        }
    }

    private static string BuildSummary(SessionTaskList list)
    {
        var pending = list.Items.Count(i => string.Equals(i.Status, AgentTaskStatuses.Pending, StringComparison.OrdinalIgnoreCase));
        var inProgress = list.Items.Count(i => string.Equals(i.Status, AgentTaskStatuses.InProgress, StringComparison.OrdinalIgnoreCase));
        var completed = list.Items.Count(i => string.Equals(i.Status, AgentTaskStatuses.Completed, StringComparison.OrdinalIgnoreCase));
        return $"Task list updated ({list.Items.Count} total: {pending} pending, {inProgress} in_progress, {completed} completed)";
    }

    private static string FormatListForAgent(SessionTaskList list)
    {
        if (list.Items.Count == 0)
        {
            return "(empty task list)";
        }

        return string.Join(
            Environment.NewLine,
            list.Items.Select(item => $"- [{item.Status}] {item.Id}: {item.Content}"));
    }
}
