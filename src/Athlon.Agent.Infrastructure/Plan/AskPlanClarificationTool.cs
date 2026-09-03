using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.Plan;

public sealed class AskPlanClarificationTool(
    IPlanRunStore planRunStore,
    IPlanPhaseAccessor phaseAccessor,
    IPlanSessionState planSessionState,
    IActiveAgentSessionContext activeSessionContext,
    IAppLogger logger) : IAgentTool, IPlanClarifyTool, IExcludedFromChildAgentToolkit
{
    private readonly IAppLogger _logger = logger.ForContext("AskPlanClarificationTool");

    public ToolDefinition Definition => new(
        Name: "ask_plan_clarification",
        Description:
            "Pause Plan Explore and ask the user one to three multiple-choice questions before drafting. "
            + "Use when the goal, stack, scope, or approach is ambiguous. Provide concrete options; "
            + "do not guess silently. The user can pick options and optionally type extra notes.",
        ParametersSchema: ToolSchema.Object()
            .Array(
                "questions",
                "1–3 questions with at least two concrete options each.",
                required: true,
                minItems: 1,
                maxItems: 3,
                items: ToolSchema.Object()
                    .String("id", "Stable kebab-case id.", required: true, minLength: 1)
                    .String("prompt", "The question shown to the user.", required: true, minLength: 1)
                    .Boolean("allow_multiple", "Whether the user may pick more than one option.", required: false, defaultValue: false)
                    .Array(
                        "options",
                        "At least two concrete choices.",
                        required: true,
                        minItems: 2,
                        items: ToolSchema.Object()
                            .String("id", "Stable kebab-case id.", required: true, minLength: 1)
                            .String("label", "Short option label.", required: true, minLength: 1)
                            .Build())
                    .Build())
            .Boolean(
                "allow_free_text",
                "Show a free-text field so the user can add notes. Defaults to true.",
                required: false,
                defaultValue: true)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var sessionId = activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ToolResult.Failure("No session", "ask_plan_clarification requires an active agent session.");
        }

        var phase = phaseAccessor.GetPhase(sessionId);
        if (phase is not PlanPhase.Explore)
        {
            return ToolResult.Failure(
                "Wrong phase",
                $"ask_plan_clarification is only available in Explore (current: {phase?.ToString() ?? "none"}).");
        }

        if (!TryParseClarification(invocation, out var clarification, out var error))
        {
            return ToolResult.Failure("Invalid questions", error);
        }

        var run = phaseAccessor.GetActiveRun(sessionId)
            ?? await planRunStore.LoadActiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return ToolResult.Failure("No plan run", "ask_plan_clarification requires an active plan run.");
        }

        run.PendingClarification = clarification;
        run.Phase = PlanPhase.AwaitClarify;
        run.Status = PlanRunStatuses.AwaitingClarification;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        phaseAccessor.SetActiveRun(run);
        planSessionState.NotifyChanged(run);
        await planRunStore.SaveActiveAsync(run, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Asked {Count} plan clarification question(s) for session {SessionId}",
            clarification.Questions.Count,
            sessionId);
        return ToolResult.Success(
            "Clarification card shown",
            $"Showed {clarification.Questions.Count} clarification question(s). End this turn.",
            endsTurn: true);
    }

    private static bool TryParseClarification(
        ToolInvocation invocation,
        out PlanClarification clarification,
        out string error)
    {
        clarification = new PlanClarification
        {
            RequestId = Guid.NewGuid().ToString("N"),
            AllowFreeText = !invocation.Arguments.TryGetBoolean("allow_free_text", out var allowFree)
                || allowFree
        };

        if (!invocation.Arguments.TryGetArray("questions", out var questionsEl)
            && !invocation.Arguments.ContainsKey("questions"))
        {
            error = "questions is required.";
            return false;
        }

        try
        {
            var raw = questionsEl.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? invocation.Arguments.GetString("questions")
                : questionsEl.GetRawText();
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "questions is required.";
                return false;
            }

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "questions must be an array.";
                return false;
            }

            var questions = new List<PlanClarificationQuestion>();
            var index = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                index++;
                if (!TryParseQuestion(item, index, out var question, out error))
                {
                    return false;
                }

                questions.Add(question);
            }

            if (questions.Count is < 1 or > 3)
            {
                error = "Provide 1–3 questions.";
                return false;
            }

            clarification.Questions = questions;
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = "Could not parse questions: " + ex.Message;
            return false;
        }
    }

    private static bool TryParseQuestion(
        JsonElement item,
        int index,
        out PlanClarificationQuestion question,
        out string error)
    {
        question = new PlanClarificationQuestion();
        if (item.ValueKind != JsonValueKind.Object)
        {
            error = $"Question {index} must be an object.";
            return false;
        }

        var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString()?.Trim() : null;
        var prompt = item.TryGetProperty("prompt", out var promptEl) ? promptEl.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(prompt))
        {
            error = $"Question {index} requires id and prompt.";
            return false;
        }

        question.Id = id;
        question.Prompt = prompt;
        question.AllowMultiple = item.TryGetProperty("allow_multiple", out var multiEl)
            && multiEl.ValueKind is JsonValueKind.True;

        if (!item.TryGetProperty("options", out var optionsEl) || optionsEl.ValueKind != JsonValueKind.Array)
        {
            error = $"Question {index} requires at least two options.";
            return false;
        }

        foreach (var optionEl in optionsEl.EnumerateArray())
        {
            if (optionEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var optionId = optionEl.TryGetProperty("id", out var oid) ? oid.GetString()?.Trim() : null;
            var label = optionEl.TryGetProperty("label", out var olabel) ? olabel.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(optionId) || string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            question.Options.Add(new PlanClarificationOption { Id = optionId, Label = label });
        }

        if (question.Options.Count < 2)
        {
            error = $"Question {index} requires at least two options.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
