using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class CreatePlanTool(
    ISessionPlanStore planStore,
    IActiveAgentSessionContext activeSessionContext,
    IPlanChangedNotifier planChangedNotifier,
    IAppLogger logger) : IAgentTool, IPlanTool, IExcludedFromChildAgentToolkit
{
    private readonly IAppLogger _logger = logger.ForContext("CreatePlanTool");

    public ToolDefinition Definition => new(
        Name: "create_plan",
        Description:
            "Create a detailed Session Plan document for the user to review before Coding. "
            + "Use after exploring the codebase with read-only tools. "
            + "Body must be full Markdown: # title, overview, sections (goals/constraints, approach and non-goals, files, steps, risks/acceptance), "
            + "and for multi-step work at least one mermaid flowchart or sequenceDiagram fence. "
            + "Provide todos aligned with implementation steps (id + content). "
            + "After calling this tool, stop and wait for the user to confirm or revise — do not edit code.",
        ParametersSchema: ToolSchema.Object()
            .String("title", "Plan title (also used as H1).", required: true, minLength: 1)
            .String("overview", "One or two sentence summary.", required: true, minLength: 1)
            .String(
                "body",
                "Full Markdown body including sections and mermaid diagram(s) for multi-step work.",
                required: true,
                minLength: 1)
            .Array(
                "todos",
                "Implementation steps to seed Coding todos after confirmation.",
                required: true,
                items: ToolSchema.Object()
                    .String("id", "Stable kebab-case id.", required: true, minLength: 1)
                    .String("content", "One verifiable step.", required: true, minLength: 1)
                    .Build(),
                minItems: 1)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var sessionId = activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ToolResult.Failure("No session", "create_plan requires an active agent session.");
        }

        var title = invocation.Arguments.GetString("title")?.Trim() ?? "";
        var overview = invocation.Arguments.GetString("overview")?.Trim() ?? "";
        var body = invocation.Arguments.GetString("body")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(overview) || string.IsNullOrWhiteSpace(body))
        {
            return ToolResult.Failure("Missing fields", "title, overview, and body are required.");
        }

        var todosResult = ParseTodos(invocation);
        if (todosResult.Error is not null)
        {
            return todosResult.Error;
        }

        var plan = new SessionPlan
        {
            Title = title,
            Overview = overview,
            Body = body,
            Todos = todosResult.Todos!,
            Status = SessionPlanStatuses.AwaitingConfirmation
        };

        try
        {
            await planStore.SaveAsync(sessionId, plan, cancellationToken).ConfigureAwait(false);
            planChangedNotifier.Notify(sessionId);
            _logger.Information("Created plan for session {SessionId}: {Title}", sessionId, title);
            return ToolResult.Success(
                "Plan created — waiting for user confirmation.",
                FormatPlanSummary(plan));
        }
        catch (Exception ex)
        {
            _logger.Warning("create_plan failed: {Error}", ex.Message);
            return ToolResult.Failure("Persist failed", ex.Message);
        }
    }

    internal static (List<SessionPlanTodoItem>? Todos, ToolResult? Error) ParseTodos(ToolInvocation invocation)
    {
        var todosJson = invocation.Arguments.TryGetArray("todos", out var todos)
            ? todos.GetRawText()
            : invocation.Arguments.GetString("todos");
        if (string.IsNullOrWhiteSpace(todosJson))
        {
            return (null, ToolResult.Failure("Missing todos", "Required parameter: todos (JSON array)"));
        }

        List<SessionPlanTodoItem> parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<SessionPlanTodoItem>>(todosJson, JsonFileStore.Options)
                ?? [];
        }
        catch (JsonException ex)
        {
            return (null, ToolResult.Failure("Invalid todos JSON", ex.Message));
        }

        if (parsed.Count == 0)
        {
            return (null, ToolResult.Failure("Empty todos", "todos must contain at least one item"));
        }

        var normalized = new List<SessionPlanTodoItem>();
        foreach (var item in parsed)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Content))
            {
                return (null, ToolResult.Failure("Invalid todo", "Each todo needs non-empty id and content"));
            }

            normalized.Add(new SessionPlanTodoItem
            {
                Id = item.Id.Trim(),
                Content = item.Content.Trim()
            });
        }

        return (normalized, null);
    }

    internal static string FormatPlanSummary(SessionPlan plan)
    {
        var todoLines = plan.Todos.Count == 0
            ? "(no todos)"
            : string.Join(Environment.NewLine, plan.Todos.Select(t => $"- {t.Id}: {t.Content}"));
        return $"Title: {plan.Title}{Environment.NewLine}"
            + $"Status: {plan.Status}{Environment.NewLine}"
            + $"Overview: {plan.Overview}{Environment.NewLine}"
            + $"Todos:{Environment.NewLine}{todoLines}{Environment.NewLine}"
            + $"Body length: {plan.Body.Length} chars";
    }
}
