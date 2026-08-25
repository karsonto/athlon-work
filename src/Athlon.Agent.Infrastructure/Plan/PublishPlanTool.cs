using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.Plan;

public sealed class PublishPlanTool(
    IPlanRunStore planRunStore,
    IPlanPhaseAccessor phaseAccessor,
    IPlanSessionState planSessionState,
    IActiveAgentSessionContext activeSessionContext,
    IAppLogger logger) : IAgentTool, IPlanDocumentTool, IExcludedFromChildAgentToolkit
{
    private readonly IAppLogger _logger = logger.ForContext("PublishPlanTool");

    public ToolDefinition Definition => new(
        Name: "publish_plan",
        Description:
            "Publish or replace the Session Plan markdown document for the current Plan-mode draft. "
            + "Call once you have explored enough and are ready for the user to review. "
            + "Provide a clear title, short overview, and a detailed body with ## Steps and ## Acceptance sections. "
            + "Optional todos become Coding task seeds after the user clicks Build.",
        ParametersSchema: ToolSchema.Object()
            .String("title", "Short plan title.", required: true, minLength: 1)
            .String("overview", "One-paragraph summary of the approach.", required: true, minLength: 1)
            .String(
                "body",
                "Markdown body including ## Steps (numbered) and ## Acceptance (checklist). May include mermaid.",
                required: true,
                minLength: 1)
            .Array(
                "todos",
                "Optional actionable todos for Coding after Build.",
                required: false,
                items: ToolSchema.Object()
                    .String("id", "Stable kebab-case id.", required: true, minLength: 1)
                    .String("content", "Verifiable step.", required: true, minLength: 1)
                    .Build())
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var sessionId = activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ToolResult.Failure("No session", "publish_plan requires an active agent session.");
        }

        var phase = phaseAccessor.GetPhase(sessionId);
        if (phase is not PlanPhase.Draft)
        {
            return ToolResult.Failure(
                "Wrong phase",
                $"publish_plan is only available in Draft (current: {phase?.ToString() ?? "none"}).");
        }

        var title = invocation.Arguments.GetString("title")?.Trim();
        var overview = invocation.Arguments.GetString("overview")?.Trim();
        var body = invocation.Arguments.GetString("body")?.Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(overview) || string.IsNullOrWhiteSpace(body))
        {
            return ToolResult.Failure("Missing fields", "title, overview, and body are required.");
        }

        var markdown = PlanDocumentParser.ComposeMarkdown(title, overview, body);
        await planRunStore.WritePlanMarkdownAsync(sessionId, markdown, cancellationToken).ConfigureAwait(false);

        var todos = ParseTodos(invocation);
        if (todos.Count == 0)
        {
            todos = PlanDocumentParser.ParseTodos(markdown).ToList();
        }

        var run = phaseAccessor.GetActiveRun(sessionId)
            ?? await planRunStore.LoadActiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (run is not null)
        {
            run.Title = title;
            run.Overview = overview;
            run.PlanMarkdown = markdown;
            run.PlanPath = planRunStore.GetPlanMarkdownPath(sessionId);
            run.Todos = todos;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            phaseAccessor.SetActiveRun(run);
            planSessionState.NotifyChanged(run);
            await planRunStore.SaveActiveAsync(run, cancellationToken).ConfigureAwait(false);
        }

        _logger.Information("Published plan for session {SessionId} ({Chars} chars)", sessionId, markdown.Length);
        return ToolResult.Success(
            "Plan published",
            $"Wrote plan.md ({markdown.Length} chars). Wait for the user to Build or Revise.");
    }

    private static List<PlanTodoItem> ParseTodos(ToolInvocation invocation)
    {
        if (!invocation.Arguments.TryGetArray("todos", out var todosEl)
            && !invocation.Arguments.ContainsKey("todos"))
        {
            return [];
        }

        try
        {
            var raw = todosEl.ValueKind == JsonValueKind.Undefined
                ? invocation.Arguments.GetString("todos")
                : todosEl.GetRawText();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            var parsed = JsonSerializer.Deserialize<List<PlanTodoItem>>(raw, JsonFileStore.Options) ?? [];
            return parsed
                .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Content))
                .Select(t => new PlanTodoItem { Id = t.Id.Trim(), Content = t.Content.Trim() })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
