using System.Text;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

internal static class TeamWorkerHelpers
{
    public static void RefreshReady(TeamTask task)
    {
        var succeeded = task.SubTasks
            .Where(s => s.Status is TeamSubTaskStatus.Succeeded)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var failedOrCancelled = task.SubTasks
            .Where(s => s.Status is TeamSubTaskStatus.Failed or TeamSubTaskStatus.Cancelled)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sub in task.SubTasks.Where(s => s.Status == TeamSubTaskStatus.Pending).ToList())
        {
            if (sub.DependsOn.Any(d => failedOrCancelled.Contains(d)))
            {
                sub.Status = TeamSubTaskStatus.Cancelled;
                sub.Error ??= "dependency failed or cancelled";
                sub.CompletedAt ??= DateTimeOffset.UtcNow;
                continue;
            }

            if (sub.DependsOn.Count == 0 || sub.DependsOn.All(d => succeeded.Contains(d)))
            {
                sub.Status = TeamSubTaskStatus.Ready;
            }
        }
    }

    public static string BuildWorkerMessage(TeamTask task, TeamSubTask sub)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Goal");
        sb.AppendLine(task.Goal);
        sb.AppendLine();
        sb.AppendLine($"## Subtask");
        sb.AppendLine($"{sub.Title} ({sub.Id}) — attempt {sub.Iteration}");
        if (!string.IsNullOrWhiteSpace(sub.RedispatchReason))
        {
            sb.AppendLine($"Redispatch reason: {sub.RedispatchReason}");
        }

        sb.AppendLine();
        sb.AppendLine("## Handoff");
        sb.AppendLine(string.IsNullOrWhiteSpace(sub.Handoff) ? "(none — use team goal)" : sub.Handoff);

        sb.AppendLine();
        sb.AppendLine("## Acceptance");
        sb.AppendLine(
            string.IsNullOrWhiteSpace(sub.AcceptanceCriteria)
                ? "(none specified — infer from handoff / goal)"
                : sub.AcceptanceCriteria);

        sb.AppendLine();
        sb.AppendLine("## Artifacts");
        if (sub.ArtifactRefs is { Count: > 0 })
        {
            foreach (var reference in sub.ArtifactRefs.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                sb.AppendLine("- " + reference.Trim());
            }
        }
        else
        {
            sb.AppendLine("(none)");
        }

        if (!string.IsNullOrWhiteSpace(sub.PrevResultSummary) && sub.Iteration > 1)
        {
            sb.AppendLine();
            sb.AppendLine("## PrevSummary");
            sb.AppendLine(sub.PrevResultSummary);
        }

        sb.AppendLine();
        sb.AppendLine("You may call team_progress / team_memory to inspect shared task state.");
        sb.AppendLine("Do not call team_dispatch / team_submit_result / sessions_*.");
        sb.AppendLine();
        sb.AppendLine("## Result format (required)");
        sb.AppendLine(
            "When finished, your last assistant message MUST be a ## Result block with a status: line "
            + "(completed|failed|blocked|needs_leader). Do not stop on a progress note.");
        return sb.ToString();
    }

    public static string Truncate(string? text, int max = 400)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    public static bool IsTerminal(TeamTaskStatus status) =>
        status is TeamTaskStatus.Succeeded or TeamTaskStatus.Failed or TeamTaskStatus.Cancelled;

    public static bool IsWorkerFinished(TeamSubTaskStatus status) =>
        status is TeamSubTaskStatus.Succeeded or TeamSubTaskStatus.Failed or TeamSubTaskStatus.Cancelled;
}
