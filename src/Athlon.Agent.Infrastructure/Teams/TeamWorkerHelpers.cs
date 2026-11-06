using System.Text;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

internal static class TeamWorkerHelpers
{
    public static void RefreshReady(TeamTask task)
    {
        var done = task.SubTasks
            .Where(s => s.Status is TeamSubTaskStatus.Succeeded)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sub in task.SubTasks.Where(s => s.Status == TeamSubTaskStatus.Pending))
        {
            if (sub.DependsOn.All(d => done.Contains(d)))
            {
                sub.Status = TeamSubTaskStatus.Ready;
            }
        }
    }

    public static string BuildWorkerMessage(TeamTask task, TeamSubTask sub)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Team goal: {task.Goal}");
        sb.AppendLine($"Subtask: {sub.Title} ({sub.Id})");
        sb.AppendLine($"Iteration: {sub.Iteration}/{sub.MaxIterations}");
        if (!string.IsNullOrWhiteSpace(sub.RedispatchReason))
        {
            sb.AppendLine($"Redispatch reason: {sub.RedispatchReason}");
        }

        if (!string.IsNullOrWhiteSpace(sub.PrevResultSummary) && sub.Iteration > 1)
        {
            sb.AppendLine();
            sb.AppendLine("Previous result summary:");
            sb.AppendLine(sub.PrevResultSummary);
        }

        sb.AppendLine();
        sb.AppendLine("Handoff:");
        sb.AppendLine(string.IsNullOrWhiteSpace(sub.Handoff) ? "(none — use team goal)" : sub.Handoff);
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
