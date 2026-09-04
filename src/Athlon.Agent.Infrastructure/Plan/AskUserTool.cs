using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.Plan;

/// <summary>
/// Pauses the agent and asks the user one to three multiple-choice questions,
/// shown in the composer QuestionBar. Works in every agent mode; when an active
/// Plan run is in Explore/Draft the run is parked in AwaitClarify so the next
/// user turn resumes it.
/// </summary>
public sealed class AskUserTool(
    IUserQuestionState userQuestions,
    IPlanPhaseAccessor phaseAccessor,
    IPlanRunStore planRunStore,
    IPlanSessionState planSessionState,
    IActiveAgentSessionContext activeSessionContext,
    IAppLogger logger) : IAgentTool, IExcludedFromChildAgentToolkit
{
    private readonly IAppLogger _logger = logger.ForContext("AskUserTool");

    public ToolDefinition Definition => new(
        Name: "ask_user",
        Description:
            "Pause and ask the user one to three multiple-choice questions before continuing. "
            + "Use when the goal, stack, scope, or approach is ambiguous and a wrong guess would "
            + "be costly. Provide concrete options; do not guess silently. The user can pick "
            + "options and optionally type extra notes. Available in every mode.",
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
            return ToolResult.Failure("No session", "ask_user requires an active agent session.");
        }

        if (!TryParseQuestion(invocation, out var question, out var error))
        {
            return ToolResult.Failure("Invalid questions", error);
        }

        userQuestions.SetPending(sessionId, question);

        // A Plan run in Explore/Draft parks in AwaitClarify until the user answers;
        // outside Plan (or with no run) the question is shown and the next turn is a
        // normal user turn that already carries the answer text.
        var run = phaseAccessor.GetActiveRun(sessionId)
            ?? await planRunStore.LoadActiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (run is { Phase: PlanPhase.Explore or PlanPhase.Draft })
        {
            run.Phase = PlanPhase.AwaitClarify;
            run.Status = PlanRunStatuses.AwaitingClarification;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            phaseAccessor.SetActiveRun(run);
            planSessionState.NotifyChanged(run);
            await planRunStore.SaveActiveAsync(run, cancellationToken).ConfigureAwait(false);
        }

        _logger.Information(
            "Asked {Count} question(s) for session {SessionId}",
            question.Questions.Count,
            sessionId);
        return ToolResult.Success(
            "Question asked",
            $"Showed {question.Questions.Count} question(s). End this turn.",
            endsTurn: true);
    }

    private static bool TryParseQuestion(
        ToolInvocation invocation,
        out UserQuestion question,
        out string error)
    {
        question = new UserQuestion
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

            var questions = new List<UserQuestionItem>();
            var index = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                index++;
                if (!TryParseItem(item, index, out var parsed, out error))
                {
                    return false;
                }

                questions.Add(parsed);
            }

            if (questions.Count is < 1 or > 3)
            {
                error = "Provide 1–3 questions.";
                return false;
            }

            question.Questions = questions;
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = "Could not parse questions: " + ex.Message;
            return false;
        }
    }

    private static bool TryParseItem(
        JsonElement item,
        int index,
        out UserQuestionItem parsed,
        out string error)
    {
        parsed = new UserQuestionItem();
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

        parsed.Id = id;
        parsed.Prompt = prompt;
        parsed.AllowMultiple = item.TryGetProperty("allow_multiple", out var multiEl)
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

            parsed.Options.Add(new UserQuestionOption { Id = optionId, Label = label });
        }

        if (parsed.Options.Count < 2)
        {
            error = $"Question {index} requires at least two options.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
