using System.Text.RegularExpressions;

namespace Athlon.Agent.Core.Teams;

public enum TeamMemberResultStatus
{
    Completed,
    Failed,
    Blocked,
    NeedsLeader,
    Invalid
}

public sealed record TeamMemberParsedResult(
    TeamMemberResultStatus Status,
    string? Verdict,
    string? FixTargetSubtaskId,
    string RawText,
    string? FindingsSummary);

public static class TeamMemberResultParser
{
    // Allow markdown bold, fullwidth colon, and a trailing period — models often wrap fields.
    private static readonly Regex StatusLine = new(
        @"^\s*(?:\*\*)?status(?:\*\*)?\s*[:：]\s*(?:\*\*)?(completed|failed|blocked|needs_leader)(?:\*\*)?\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex VerdictLine = new(
        @"^\s*(?:\*\*)?verdict(?:\*\*)?\s*[:：]\s*(?:\*\*)?(pass|fail)(?:\*\*)?\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex FixTargetLine = new(
        @"^\s*(?:\*\*)?fix_target_subtask_id(?:\*\*)?\s*[:：]\s*(\S+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly string[] PreambleOnly =
    [
        "let me open a terminal",
        "i'll start by",
        "i will start by",
        "i'm going to start",
        "i am going to start",
        "let me explore",
        "let me check",
        "let me inspect",
        "let me begin",
        "i'll begin",
        "i'll inspect",
        "first, i'll",
        "first i'll"
    ];

    public static TeamMemberParsedResult Parse(string? responseText)
    {
        var text = (responseText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)
            || text.Contains("(Team member finished without assistant text.)", StringComparison.OrdinalIgnoreCase))
        {
            return new TeamMemberParsedResult(
                TeamMemberResultStatus.Invalid,
                Verdict: null,
                FixTargetSubtaskId: null,
                RawText: text,
                FindingsSummary: null);
        }

        if (LooksLikePreambleOnly(text))
        {
            return new TeamMemberParsedResult(
                TeamMemberResultStatus.Invalid,
                Verdict: null,
                FixTargetSubtaskId: null,
                RawText: text,
                FindingsSummary: null);
        }

        var statusMatch = StatusLine.Match(text);
        var verdictMatch = VerdictLine.Match(text);
        var fixTargetMatch = FixTargetLine.Match(text);
        var verdict = verdictMatch.Success ? verdictMatch.Groups[1].Value.Trim().ToLowerInvariant() : null;
        var fixTarget = fixTargetMatch.Success ? fixTargetMatch.Groups[1].Value.Trim() : null;
        var findings = ExtractFindings(text);

        // Explicit verdict=fail wins even without a status line (verification workers).
        if (string.Equals(verdict, "fail", StringComparison.OrdinalIgnoreCase))
        {
            return new TeamMemberParsedResult(
                TeamMemberResultStatus.Failed,
                verdict,
                fixTarget,
                text,
                findings);
        }

        if (string.Equals(verdict, "pass", StringComparison.OrdinalIgnoreCase)
            && !statusMatch.Success)
        {
            return new TeamMemberParsedResult(
                TeamMemberResultStatus.Completed,
                verdict,
                fixTarget,
                text,
                findings);
        }

        // Workers must emit an explicit status: line — no length / ## Result heuristics.
        if (!statusMatch.Success)
        {
            return new TeamMemberParsedResult(
                TeamMemberResultStatus.Invalid,
                Verdict: verdict,
                FixTargetSubtaskId: fixTarget,
                RawText: text,
                FindingsSummary: findings);
        }

        var statusToken = statusMatch.Groups[1].Value.Trim().ToLowerInvariant();
        var status = statusToken switch
        {
            "completed" => TeamMemberResultStatus.Completed,
            "failed" => TeamMemberResultStatus.Failed,
            "blocked" => TeamMemberResultStatus.Blocked,
            "needs_leader" => TeamMemberResultStatus.NeedsLeader,
            _ => TeamMemberResultStatus.Invalid
        };

        return new TeamMemberParsedResult(status, verdict, fixTarget, text, findings);
    }

    public static bool IsSuccessful(TeamMemberParsedResult parsed) =>
        parsed.Status == TeamMemberResultStatus.Completed;

    private static bool LooksLikePreambleOnly(string text)
    {
        var compact = text.ReplaceLineEndings(" ").Trim();
        if (compact.Length > 160)
        {
            return false;
        }

        var lower = compact.ToLowerInvariant();
        return PreambleOnly.Any(p => lower.StartsWith(p, StringComparison.Ordinal) || lower.Contains(p, StringComparison.Ordinal));
    }

    private static string? ExtractFindings(string text)
    {
        const string marker = "findings:";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var slice = text[(idx + marker.Length)..].Trim();
        if (slice.Length == 0)
        {
            return null;
        }

        return Truncate(slice, 1500);
    }

    private static string Truncate(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
