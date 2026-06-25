using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class TodoWriteTool(
    ISessionTaskStore taskStore,
    IActiveAgentSessionContext activeSessionContext) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "todo_write",
        "Replace the full task list for this session. Use for multi-step work tracking. Only one task may be in_progress.",
        new Dictionary<string, string>
        {
            ["tasks"] = "JSON array of {id, content, status} where status is pending|in_progress|completed|cancelled"
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "tasks", out var tasksJson, out var error))
        {
            return error;
        }

        var sessionId = activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ToolResult.Failure("No session", "todo_write requires an active agent session.");
        }

        List<AgentTaskItem> tasks;
        try
        {
            tasks = ParseTasks(tasksJson);
        }
        catch (Exception ex)
        {
            return ToolResult.Failure("Invalid tasks JSON", ex.Message);
        }

        var inProgress = tasks.Count(task => task.Status == AgentTaskStatus.InProgress);
        if (inProgress > 1)
        {
            return ToolResult.Failure(
                "Invalid task list",
                "Only one task may be in_progress at a time.");
        }

        await taskStore.SaveAsync(sessionId, tasks, cancellationToken);
        var summary = tasks.Count == 0
            ? "Cleared task list"
            : $"Saved {tasks.Count} tasks ({tasks.Count(task => task.Status == AgentTaskStatus.Completed)} completed)";
        return ToolResult.Success(summary, FormatTasks(tasks));
    }

    private static List<AgentTaskItem> ParseTasks(string tasksJson)
    {
        using var document = JsonDocument.Parse(tasksJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("tasks must be a JSON array.");
        }

        var tasks = new List<AgentTaskItem>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var id = element.GetProperty("id").GetString() ?? string.Empty;
            var content = element.GetProperty("content").GetString() ?? string.Empty;
            var statusText = element.GetProperty("status").GetString() ?? "pending";
            if (!Enum.TryParse<AgentTaskStatus>(statusText, ignoreCase: true, out var status))
            {
                throw new InvalidOperationException($"Unknown status '{statusText}'.");
            }

            tasks.Add(new AgentTaskItem(id.Trim(), content.Trim(), status));
        }

        return tasks;
    }

    private static string FormatTasks(IReadOnlyList<AgentTaskItem> tasks)
    {
        if (tasks.Count == 0)
        {
            return "(empty task list)";
        }

        return string.Join(
            Environment.NewLine,
            tasks.Select(task => $"- [{task.Status}] {task.Id}: {task.Content}"));
    }
}
