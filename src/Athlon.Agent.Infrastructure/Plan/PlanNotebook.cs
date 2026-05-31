using System.Collections.Concurrent;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Infrastructure.Plan;

public sealed class PlanNotebook(AppSettings settings, WorkspaceGuard workspaceGuard) : IPlanNotebook
{
    private readonly ConcurrentDictionary<string, AgentPlan> _plans = new(StringComparer.Ordinal);

    public AgentPlan? GetCurrent(string sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : _plans.GetValueOrDefault(sessionId);

    public PlanOperationResult CreatePlan(string sessionId, CreatePlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new PlanOperationResult(false, "No active session.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new PlanOperationResult(false, "Plan name is required.");
        }

        if (request.Subtasks.Count == 0)
        {
            return new PlanOperationResult(false, "At least one subtask is required.");
        }

        if (request.Subtasks.Count > settings.Plan.MaxSubtasks)
        {
            return new PlanOperationResult(
                false,
                $"Cannot create plan: {request.Subtasks.Count} subtasks exceeds the maximum of {settings.Plan.MaxSubtasks}.");
        }

        var subtasks = request.Subtasks
            .Select(input => new AgentSubTask(
                input.Name,
                input.Description ?? string.Empty,
                input.ExpectedOutcome ?? string.Empty))
            .ToList();

        subtasks[0].State = SubTaskState.InProgress;

        var previous = _plans.TryGetValue(sessionId, out var existing) ? existing.Name : null;
        var plan = new AgentPlan(
            request.Name.Trim(),
            request.Description?.Trim() ?? string.Empty,
            request.ExpectedOutcome?.Trim() ?? string.Empty,
            subtasks);

        _plans[sessionId] = plan;

        var message = previous is null
            ? $"Plan '{plan.Name}' created successfully."
            : $"The current plan named '{previous}' is replaced by the newly created plan named '{plan.Name}'.";

        message += AppendSyncNote(SyncPlanFile(plan));
        return new PlanOperationResult(true, message);
    }

    public PlanOperationResult FinishSubtask(string sessionId, int subtaskIndex, string outcome)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new PlanOperationResult(false, "No active session.");
        }

        if (!_plans.TryGetValue(sessionId, out var plan))
        {
            return new PlanOperationResult(false, "There is no active plan. Call create_plan first.");
        }

        var subtasks = plan.Subtasks.ToList();
        if (subtaskIndex < 0 || subtaskIndex >= subtasks.Count)
        {
            return new PlanOperationResult(
                false,
                $"Invalid subtask_idx '{subtaskIndex}'. Must be between 0 and {subtasks.Count - 1}.");
        }

        for (var index = 0; index < subtaskIndex; index++)
        {
            var previous = subtasks[index];
            if (previous.State is not SubTaskState.Done and not SubTaskState.Abandoned)
            {
                return new PlanOperationResult(
                    false,
                    $"Cannot finish subtask at index {subtaskIndex} because the previous subtask "
                    + $"(at index {index}) named '{previous.Name}' is not done yet. Finish previous subtasks first.");
            }
        }

        if (string.IsNullOrWhiteSpace(outcome))
        {
            return new PlanOperationResult(false, "subtask_outcome is required.");
        }

        var current = subtasks[subtaskIndex];
        current.Finish(outcome.Trim());

        string message;
        if (subtaskIndex + 1 < subtasks.Count)
        {
            var next = subtasks[subtaskIndex + 1];
            next.State = SubTaskState.InProgress;
            message =
                $"Subtask (at index {subtaskIndex}) named '{current.Name}' is marked as done successfully. "
                + $"The next subtask (at index {subtaskIndex + 1}) named '{next.Name}' is activated.";
        }
        else
        {
            message =
                $"Subtask (at index {subtaskIndex}) named '{current.Name}' is marked as done successfully.";
        }

        _plans[sessionId] = new AgentPlan(plan.Name, plan.Description, plan.ExpectedOutcome, subtasks);
        message += AppendSyncNote(SyncPlanFile(_plans[sessionId]));
        return new PlanOperationResult(true, message);
    }

    public string GetPlanMarkdown(string sessionId, bool detailed = true)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "No active session.";
        }

        if (!_plans.TryGetValue(sessionId, out var plan))
        {
            return "There is no active plan. Call create_plan first.";
        }

        return PlanMarkdownFormatter.ToMarkdown(plan, detailed);
    }

    private string SyncPlanFile(AgentPlan plan)
    {
        if (!workspaceGuard.TryGetWorkspaceRoot(out var rootPath))
        {
            return "not_written";
        }

        var fileName = string.IsNullOrWhiteSpace(settings.Plan.PlanFileName)
            ? "plan.md"
            : settings.Plan.PlanFileName;
        var path = Path.Combine(rootPath, fileName);
        var markdown = PlanMarkdownFormatter.ToMarkdown(plan, detailed: true);
        File.WriteAllText(path, markdown + Environment.NewLine);
        return path;
    }

    private static string AppendSyncNote(string syncResult) =>
        syncResult switch
        {
            "not_written" => " (plan kept in session memory only; no workspace configured for plan.md sync.)",
            _ => $" (Synced to {syncResult}.)"
        };
}
