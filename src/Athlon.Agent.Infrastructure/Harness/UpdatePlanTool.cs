using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class UpdatePlanTool(
    ISessionPlanStore planStore,
    IActiveAgentSessionContext activeSessionContext,
    IPlanChangedNotifier planChangedNotifier,
    IAppLogger logger) : IAgentTool, IPlanTool, IExcludedFromChildAgentToolkit
{
    private readonly IAppLogger _logger = logger.ForContext("UpdatePlanTool");

    public ToolDefinition Definition => new(
        Name: "update_plan",
        Description:
            "Update the current Session Plan after user revision feedback. "
            + "Keep a full detailed Markdown body (not a thin outline) and refresh mermaid diagrams when steps change. "
            + "Status returns to awaiting_confirmation. Stop after updating and wait for confirmation.",
        ParametersSchema: ToolSchema.Object()
            .String("title", "Updated plan title.", required: true, minLength: 1)
            .String("overview", "Updated one or two sentence summary.", required: true, minLength: 1)
            .String(
                "body",
                "Full updated Markdown body with sections and mermaid diagram(s) for multi-step work.",
                required: true,
                minLength: 1)
            .Array(
                "todos",
                "Updated implementation steps for Coding seed.",
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
            return ToolResult.Failure("No session", "update_plan requires an active agent session.");
        }

        var title = invocation.Arguments.GetString("title")?.Trim() ?? "";
        var overview = invocation.Arguments.GetString("overview")?.Trim() ?? "";
        var body = invocation.Arguments.GetString("body")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(overview) || string.IsNullOrWhiteSpace(body))
        {
            return ToolResult.Failure("Missing fields", "title, overview, and body are required.");
        }

        var todosResult = CreatePlanTool.ParseTodos(invocation);
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
            _logger.Information("Updated plan for session {SessionId}: {Title}", sessionId, title);
            return ToolResult.Success(
                "Plan updated — waiting for user confirmation.",
                CreatePlanTool.FormatPlanSummary(plan));
        }
        catch (Exception ex)
        {
            _logger.Warning("update_plan failed: {Error}", ex.Message);
            return ToolResult.Failure("Persist failed", ex.Message);
        }
    }
}
