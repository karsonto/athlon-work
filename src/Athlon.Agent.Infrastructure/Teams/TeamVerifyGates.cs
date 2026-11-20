using System.Text.RegularExpressions;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

internal static class TeamVerifyGates
{
    private static readonly Regex CodeGoalHint = new(
        @"\b(fix|implement|refactor|patch|pr\b|pull request|build|compile|unit test|tests?|bug)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex VerdictLine = new(
        @"^\s*(?:\*\*)?verdict(?:\*\*)?\s*[:：]\s*(?:\*\*)?(pass|fail)(?:\*\*)?\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static bool LooksLikeCodeGoal(TeamTask task)
    {
        if (CodeGoalHint.IsMatch(task.Goal ?? ""))
        {
            return true;
        }

        foreach (var sub in task.SubTasks)
        {
            if (CodeGoalHint.IsMatch(sub.Title ?? "")
                || CodeGoalHint.IsMatch(sub.Handoff ?? "")
                || CodeGoalHint.IsMatch(sub.AcceptanceCriteria ?? ""))
            {
                return true;
            }
        }

        return false;
    }

    public static bool AnyVerdictPresent(TeamTask task) =>
        task.SubTasks.Any(s =>
            (!string.IsNullOrWhiteSpace(s.Result) && VerdictLine.IsMatch(s.Result))
            || (!string.IsNullOrWhiteSpace(s.PrevFindings) && VerdictLine.IsMatch(s.PrevFindings)));

    public static bool HasSucceededVerifyPass(TeamTask task)
    {
        foreach (var sub in task.SubTasks.Where(s => s.Status == TeamSubTaskStatus.Succeeded))
        {
            if (string.IsNullOrWhiteSpace(sub.Result))
            {
                continue;
            }

            var parsed = TeamMemberResultParser.Parse(sub.Result);
            if (string.Equals(parsed.Verdict, "pass", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool RequiresVerifyPass(TeamTask task) =>
        LooksLikeCodeGoal(task) || AnyVerdictPresent(task);
}
